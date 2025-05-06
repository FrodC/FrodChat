using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace FrodChat.Hubs
{
    public class ChatHub : Hub
    {
        private const string AdminPassword = "x192838x";
        private record GroupInfo(string Password);

        private static readonly ConcurrentDictionary<string, GroupInfo> GroupsInfo = new();
        private static readonly ConcurrentDictionary<string, string> UserNames = new();
        private static readonly ConcurrentDictionary<string, string> UserRooms = new();
        private static readonly ConcurrentDictionary<string, byte> AdminConns = new();

        public async Task CreateOrJoinGroup(string room, string user, string? pwd = null)
        {
            if (string.IsNullOrWhiteSpace(room)) throw new HubException("Oda adı boş olamaz!");
            if (string.IsNullOrWhiteSpace(user)) throw new HubException("Kullanıcı adı boş olamaz!");
            
            user = user.Trim();
            // Oda adını temizle ve validasyon yap
            room = (room ?? "").Trim();
            if (string.IsNullOrWhiteSpace(room))
            {
                await Clients.Caller.SendAsync("ReceiveError", "Oda adı boş olamaz!");
                return;
            }

            // Aynı odaya tekrar katılma kontrolü
            if (UserRooms.TryGetValue(Context.ConnectionId, out var currentRoom) &&
                currentRoom.Equals(room, StringComparison.OrdinalIgnoreCase))
            {
                await Clients.Caller.SendAsync("ReceiveError", "Zaten bu odadasınız!");
                return;
            }

            // Eski odadan çık
            if (UserRooms.TryGetValue(Context.ConnectionId, out var oldRoom))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoom);
                await Clients.Group(oldRoom).SendAsync("ReceiveMessage", $"<em>{user} ayrıldı.</em>");

                if (UserRooms.Values.Count(r => r == oldRoom) == 1)
                    GroupsInfo.TryRemove(oldRoom, out _);
            }

            // Şifre kontrolü
            var info = GroupsInfo.GetOrAdd(room, _ => new GroupInfo(pwd ?? string.Empty));
            if (info.Password != (pwd ?? string.Empty))
            {
                await Clients.Caller.SendAsync("ReceiveError", "Geçersiz şifre!");
                return;
            }

            // Nick çakışması kontrolü
            bool nickUsed = UserNames.Any(kv =>
                kv.Value.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                UserRooms.TryGetValue(kv.Key, out var r) &&
                r == room
            );

            if (nickUsed)
            {
                await Clients.Caller.SendAsync("ReceiveError", "Bu isim zaten kullanılıyor!");
                return;
            }

            // Yeni odaya ekle
            UserNames[Context.ConnectionId] = user;
            UserRooms[Context.ConnectionId] = room;

            await Groups.AddToGroupAsync(Context.ConnectionId, room);
            await Clients.Group(room).SendAsync("ReceiveMessage", $"<em>{user} katıldı!</em>");
            await BroadcastGroupList();
            await Clients.Caller.SendAsync("RoomJoined", room);
        }

        public async Task SendMessage(string room, string user, string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            if (msg.StartsWith("/"))
            {
                await HandleCommand(msg, room, user);
                return;
            }

            bool isAdmin = AdminConns.ContainsKey(Context.ConnectionId);
            string safeMsg = WebUtility.HtmlEncode(msg);

            await Clients.Group(room).SendAsync(
                "ReceiveMessage",
                $"{(isAdmin ? "<span class='admin-badge'>ADMİN</span> " : "")}<strong>{user}</strong>: {safeMsg}"
            );
        }

        private async Task HandleCommand(string cmd, string room, string user)
        {
            var parts = cmd.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var key = parts[0].ToLowerInvariant();
            switch (key)
            {
                case "/admin" when parts.Length > 1 && parts[1] == AdminPassword:
                    AdminConns[Context.ConnectionId] = 1;
                    await Clients.Caller.SendAsync("ReceiveMessage", "<em>Admin yetkisi verildi!</em>");
                    break;

                case "/kick" when AdminConns.ContainsKey(Context.ConnectionId) && parts.Length > 1:
                    var targetEntry = UserNames
                        .FirstOrDefault(kv =>
                            kv.Value.Equals(parts[1], StringComparison.OrdinalIgnoreCase) &&
                            UserRooms.TryGetValue(kv.Key, out var r) && r == room);
                    if (!string.IsNullOrEmpty(targetEntry.Key))
                    {
                        await RemoveUser(targetEntry.Key);

                        // 2. "Kicked" event'ini gönder
                        await Clients.Client(targetEntry.Key).SendAsync("Kicked", room);
                    }
                    break;

                case "/nick" when parts.Length > 1:
                    UserNames[Context.ConnectionId] = parts[1];
                    await Clients.Group(room).SendAsync("ReceiveMessage", $"<em>{user} → {parts[1]}</em>");
                    break;

                default:
                    await Clients.Caller.SendAsync("ReceiveHelp");
                    break;
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await RemoveUser(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        private async Task RemoveUser(string connId)
        {
            if (!UserRooms.TryRemove(connId, out var room) || string.IsNullOrEmpty(room)) return;

            UserNames.TryRemove(connId, out var user);
            AdminConns.TryRemove(connId, out _);

            await Groups.RemoveFromGroupAsync(connId, room);
            await Clients.Group(room).SendAsync("ReceiveMessage", $"<em>{user ?? "Kullanıcı"} ayrıldı.</em>");

            if (!UserRooms.Values.Any(r => r == room))
                GroupsInfo.TryRemove(room, out _);

            await BroadcastGroupList();
        }

        private Task BroadcastGroupList() => Clients.All.SendAsync("UpdateGroupList",
            GroupsInfo
                .Where(g => !string.IsNullOrWhiteSpace(g.Key.Trim())) // Boş veya sadece boşluk içerenleri filtrele
                .Select(g => new
                {
                    name = g.Key.Trim(), // camelCase & Trim
                    members = UserRooms.Count(r => r.Value.Equals(g.Key, StringComparison.OrdinalIgnoreCase)),
                    password = !string.IsNullOrEmpty(g.Value.Password)
                })
                .ToList());
    }
}