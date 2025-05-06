const getElement = id => document.getElementById(id);

let connection, userName, currentRoom;

getElement('joinBtn').onclick = async () => {
    userName = getElement('username').value.trim();
    const room = getElement('room').value.trim();
    const pwd = getElement('pwd').value;

    if (!userName || !room) {
        getElement('error').textContent = "Kullanıcı adı ve oda zorunlu!";
        return;
    }

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/chatHub")
        .withAutomaticReconnect()
        .build();

    connection.onclose(() => {
        getElement('error').textContent = "Bağlantı koptu. Tekrar bağlanılıyor...";
    });

    connection.on("ReceiveMessage", msg => {
        const messagesDiv = getElement('messages');
        const msgDiv = document.createElement('div');
        msgDiv.innerHTML = msg;
        messagesDiv.appendChild(msgDiv);
        messagesDiv.scrollTop = messagesDiv.scrollHeight;
    });

    connection.on("ReceiveError", msg => {
        getElement('error').textContent = msg;
    });

    connection.on("Kicked", (roomName) => {
        getElement('messages').innerHTML = "";

        getElement('chat').hidden = true;
        getElement('login').hidden = false;
        getElement('error').textContent = `${roomName} odasından atıldınız!`;

        // 3. Bağlantıyı yeniden başlat
        connection.stop().then(() => {
            connection.start().catch(err =>
                console.error("Yeniden bağlantı hatası:", err)
            );
        });
    });
   
    connection.on("UpdateGroupList", groups => {
        const groupList = getElement('groupList');
        groupList.innerHTML = '<h3>Aktif Odalar</h3>';

        groups.forEach(g => {
            // Artık fallback kullanma, direkt sunucudan gelen veriye güven
            const roomName = g.name;
            const memberCount = g.members;
            const isLocked = g.password ? '🔒' : '';

            const div = document.createElement('div');
            div.className = 'room-item';
            div.innerHTML = `${roomName} (${memberCount} üye) ${isLocked}`;

            div.onclick = async () => {
                if (roomName === currentRoom) return;
                try {
                    getElement('messages').innerHTML = "";
                    const password = g.password ? prompt('Şifre:') : '';
                    await connection.invoke("CreateOrJoinGroup", roomName, userName, password);
                } catch (err) {
                    getElement('error').textContent = err.toString();
                }
            };
            groupList.appendChild(div);
        });
    });

    connection.on("RoomJoined", roomName => {
        currentRoom = roomName;
        getElement('currentRoom').textContent = roomName;
        getElement('chat').hidden = false;
        getElement('login').hidden = true;
    });

    connection.on("ReceiveHelp", () => {
        alert("Komutlar:\n/admin [şifre] - Admin ol\n/kick [kullanıcı] - Kullanıcıyı at\n/nick [yeni ad] - İsim değiştir");
    });

    try {
        await connection.start();
        await connection.invoke("CreateOrJoinGroup", room, userName, pwd);
    } catch (e) {
        getElement('error').textContent = "Bağlantı hatası: " + e.toString();
    }
};

getElement('sendBtn').onclick = async () => {
    const msg = getElement('messageInput').value.trim();
    if (!msg) return;

    try {
        await connection.invoke("SendMessage", currentRoom, userName, msg);
        getElement('messageInput').value = '';
    } catch (e) {
        console.error("Mesaj gönderilemedi:", e);
    }
};

getElement('messageInput').addEventListener('keypress', e => {
    if (e.key === 'Enter') {
        e.preventDefault();
        getElement('sendBtn').click();
    }
});

getElement('helpBtn').onclick = () => {
    connection.invoke("SendMessage", currentRoom, userName, "/help");
};