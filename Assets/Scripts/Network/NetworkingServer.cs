using AliveOpsArcade.OdinSerializer;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class NetworkingServer : INetworking
{
    // Thread and safety
    Thread serverThread;

    public object packetQueueLock { get; set; } = new object();

    public object loadSceneLock { get; set; } = new object();
    public bool triggerLoadScene { get; set; } = false;

    public object clientDisconnectLock { get; set; } = new object();
    public bool triggerClientDisconected { get; set; } = false;

    // Network
    Socket serverSocket;
    EndPoint endPoint;

    float updateTime = 0.05f;

    int channel1Port = 9050;
    int channel2Port = 9051;

    // UserData & Players
    public User myUserData { get; set; }

    public Queue<Packet> packetQueue { get; set; }

    // Dictionary to link a network ID (of a client) with an endpoint (server not included)
    Dictionary<string, EndPoint> clients;

    // Relates network ID with its player object (the world state basically
    // used as server to receive world data and send it to clients
    Dictionary<string, PlayerObject> playerMap;
    Dictionary<string, EnemyObject> enemiesMap;

    float elapsedUpdateTime = 0f;

    public void Start()
    {
        clients = new Dictionary<string, EndPoint>();

        packetQueue = new Queue<Packet>();

        playerMap = new Dictionary<string, PlayerObject>();
        enemiesMap = new Dictionary<string, EnemyObject>();

        InitializeSocket();
    }

    // Initializes the server socket in UDP and starts the thread that is constantly listening for messages
    private void InitializeSocket()
    {
        Debug.Log("[SERVER] Server Initializing...");
        Debug.Log("[SERVER] Creating Socket...");

        serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Debug.Log("[SERVER] Socket Created...");

        IPEndPoint ipep = new IPEndPoint(IPAddress.Any, channel1Port);
        serverSocket.Bind(ipep);
        Debug.Log("[SERVER] Socket Binded...");

        endPoint = new IPEndPoint(IPAddress.Any, channel2Port);

        Debug.Log("SERVER PLAYER IS SPAWNING");
        NetworkingManager.Instance.Spawn(myUserData.networkID);

        serverThread = new Thread(ServerListener);
        serverThread.IsBackground = true;
        serverThread.Start();
    }

    // Servers starts listening and when a package is received it executes OnPackageReceived function
    private void ServerListener()
    {
        Debug.Log("[SERVER] Server started listening");

        while (true)
        {
            // Listen for data
            byte[] data = new byte[1024];
            int recv = serverSocket.ReceiveFrom(data, ref endPoint);

            Debug.Log("[SERVER] package received");

            // Call OnPackageReceived
            // Whenever a package is received, we want to parse the message
            OnPackageReceived(data, recv, endPoint);
        }
    }

    // Handles packages received
    public void OnPackageReceived(byte[] inputPacket, int recv, EndPoint fromAddress)
    {
        Packet packet = SerializationUtility.DeserializeValue<Packet>(inputPacket, DataFormat.JSON);

        // If the client that sent a message to this server is new, add it to the list of clients.
        if (packet.type == PacketType.HELLO)
        {
            // When a client sends a hello packet it is saved in the clients dictionary.
            HelloPacket helloPacket = SerializationUtility.DeserializeValue<HelloPacket>(inputPacket, DataFormat.JSON);

            if (!clients.ContainsKey(helloPacket.clientData.networkID))
                clients.Add(helloPacket.clientData.networkID, fromAddress);

            lock (packetQueueLock)
            {
                packetQueue.Enqueue(helloPacket);
            }
        }
        else if (packet.type == PacketType.WORLD_STATE)
        {
            ClientPacket clientPacket = SerializationUtility.DeserializeValue<ClientPacket>(inputPacket, DataFormat.JSON);

            lock (packetQueueLock)
            {
                packetQueue.Enqueue(clientPacket);
            }
        }
    }

    public void OnUpdate()
    {
        // Limit to 10 packets per second to mitigate lag
        elapsedUpdateTime += Time.deltaTime;

        if (elapsedUpdateTime >= updateTime)
        {
            UpdatePlayersState(NetworkingManager.Instance.playerGOMap);
            UpdateEnemiesState(GameObject.FindGameObjectsWithTag("Enemy"));

            ServerPacket serverPacket = new ServerPacket(PacketType.WORLD_STATE, playerMap, enemiesMap);

            byte[] dataBroadcast = SerializationUtility.SerializeValue(serverPacket, DataFormat.JSON);

            BroadcastPacket(dataBroadcast, false);

            playerMap[myUserData.networkID].hasShot = false;

            elapsedUpdateTime = elapsedUpdateTime % 1f;
        }
    }

    public void UpdatePlayersState(Dictionary<string, GameObject> players)
    {
        foreach (var player in players)
        {
            string id = player.Value.GetComponent<NetworkObject>().networkID;

            if (playerMap.ContainsKey(id))
            {
                playerMap[id].action = PlayerObject.Action.UPDATE;
                playerMap[id].position = player.Value.transform.position;
                playerMap[id].rotation = player.Value.transform.rotation;
                playerMap[id].isRunning = player.Value.GetComponent<PlayerController>().isMovementPressed;
            }
            else
            {
                PlayerObject playerObject = new PlayerObject(
                    PlayerObject.Action.CREATE,
                    player.Value.transform.position,
                    player.Value.transform.rotation,
                    player.Value.GetComponent<PlayerController>().isMovementPressed
                    );

                playerMap.Add(id, playerObject);
            }
        }
    }

    public void UpdateEnemiesState(GameObject[] enemies)
    {
        foreach (GameObject enemy in enemies)
        {
            string id = enemy.GetComponent<NetworkObject>().networkID;

            if (enemiesMap.ContainsKey(id))
            {
                enemiesMap[id].action = EnemyObject.Action.UPDATE;
                enemiesMap[id].position = enemy.transform.position;
                enemiesMap[id].rotation = enemy.transform.rotation;
            }
            else
            {
                EnemyObject enemyObject = new EnemyObject(
                    enemy.GetComponent<NetworkObject>().networkID,
                    EnemyObject.Action.CREATE,
                    enemy.transform.position,
                    enemy.transform.rotation
                    );

                enemiesMap.Add(id, enemyObject);
            }
        }
    }

    public void SendPacket(byte[] outputPacket, EndPoint toAddress)
    {
        serverSocket.SendTo(outputPacket, outputPacket.Length, SocketFlags.None, toAddress);
    }

    public void BroadcastPacket(byte[] data, bool fromClient) // True: doesn't include the client that sent the packet in the broadcast
    {
        // Broadcast the message to the other clients
        foreach (var entry in clients)
        {
            if (fromClient && entry.Value.Equals(endPoint))
                continue;

            SendPacket(data, entry.Value);
        }
    }

    public void NotifySpawn(string networkID)
    {
        // If the new spawned object is a client
        if (clients.ContainsKey(networkID))
        {
            // Broadcast to all the other active clients that a player has joined
            if (clients.Count != 0)
            {
                //we send a packet with the player map to the client to copy
                ServerPacket serverPacket = new ServerPacket(PacketType.WORLD_STATE, playerMap, enemiesMap);

                byte[] dataToBroadcast = SerializationUtility.SerializeValue(serverPacket, DataFormat.JSON);
                BroadcastPacket(dataToBroadcast, true);
            }

            // To our newly joined client we send the welcome packet with the player map to be copied and start the spawning process
            // A copy of the players' map to be sent to the new client but with all players sent to create since the new client doesn't have any
            Dictionary<string, PlayerObject> welcomePlayerMap = new Dictionary<string, PlayerObject>();
            Dictionary<string, EnemyObject> welcomeEnemyMap = new Dictionary<string, EnemyObject>();

            foreach (var entry in playerMap)
            {
                // Set all player objects to be created
                PlayerObject newObj = new PlayerObject(PlayerObject.Action.CREATE, entry.Value.position, entry.Value.rotation, entry.Value.isRunning, entry.Value.hasShot);
                welcomePlayerMap.Add(entry.Key, newObj);
            }
            foreach (var enemy in enemiesMap)
            {
                EnemyObject newObj = new EnemyObject(enemy.Value.networkID, EnemyObject.Action.CREATE, enemy.Value.position, enemy.Value.rotation);
                welcomeEnemyMap.Add(enemy.Key, newObj);
            }

            // Prepare the packet to be sent back notifying the connection
            ServerPacket welcomePacket = new ServerPacket(PacketType.WELCOME, welcomePlayerMap, welcomeEnemyMap);

            byte[] dataWelcome = SerializationUtility.SerializeValue(welcomePacket, DataFormat.JSON);

            SendPacket(dataWelcome, clients[networkID]);
        }
    }

    public void NotifySceneChange(string sceneName)
    {
        ServerPacket serverPacket = new ServerPacket(PacketType.GAME_START, playerMap, enemiesMap);

        byte[] data = SerializationUtility.SerializeValue(serverPacket, DataFormat.JSON);

        BroadcastPacket(data, false);
    }

    public void LoadScene(string sceneName)
    {
        lock (loadSceneLock)
        {
            triggerLoadScene = true;
        }
    }

    public void OnSceneLoaded()
    {
        NetworkingManager.Instance.Spawn(myUserData.networkID);

        // Broadcast the corresponding message to the clients
        NotifySceneChange("Game");
    }

    public void OnShoot()
    {
        playerMap[myUserData.networkID].hasShot = true;
    }

    public void OnConnectionReset(EndPoint fromAddress)
    {
        throw new System.NotImplementedException();
    }

    public void OnDisconnect()
    {
        Debug.Log("Destroying Scene");

        serverSocket.Close();
        serverThread.Abort();
    }

    public void reportError()
    {
        throw new System.NotImplementedException();
    }
}