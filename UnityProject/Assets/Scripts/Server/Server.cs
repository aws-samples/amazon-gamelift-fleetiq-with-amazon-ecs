// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Net;
using System.Net.Sockets;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using Amazon.GameLift;
using Amazon.GameLift.Model;
using Amazon;

[Serializable]
public class TaskData
{
    public string TaskARN;
}

[Serializable]
public class TaskStatusData
{
    public string taskArn { get; set; } //arn of the task the server is running on (withut the container ID)
}

// *** MONOBEHAVIOUR TO MANAGE SERVER LOGIC *** //

public class Server : MonoBehaviour
{
    public GameObject playerPrefab;

#if SERVER

    // TODO: Update this to your selected Region
    public RegionEndpoint regionEndpoint = RegionEndpoint.USEast1;

    // List of players
    public List<NetworkPlayer> players = new List<NetworkPlayer>();
    public int rollingPlayerId = 0; //Rolling player id that is used to give new players an ID when connecting

    // For testing we have maximum of 2 players
    public static int maxPlayers = 2;

    public static int port = -1;
    public static string fleetIqGameServerGroup = null;

    //We get events back from the NetworkServer through this static list
    public static List<SimpleMessage> messagesToProcess = new List<SimpleMessage>();

    // Game server data
    private string publicIP = null;
    private string taskDataArn = null;
    private string taskDataArnWithContainer = null;
    private string gameServerId = null;
    private string instanceId = null;
    public string GetGameServerId() { return gameServerId; }

    static float fleetIqUpdateInterval = 60.0f;
    float fleetIqUpdateCounter = 0.0f;
    bool registeredToFleetIQ = false;

    private int lastPlayerCount = 0;

    NetworkServer server;

    // Game state
    private bool gameStarted = false;

    // Helper function to check if a player exists in the enemy list already
    private bool PlayerExists(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                return true;
            }
        }
        return false;
    }

    // Helper function to find a player from the enemy list
    private NetworkPlayer GetPlayer(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                return player;
            }
        }
        return null;
    }

    public void RemovePlayer(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                player.DeleteGameObject();
                players.Remove(player);
                return;
            }
        }
    }

    public void StartGame()
    {
        System.Console.WriteLine("Starting game");
        this.gameStarted = true;
    }

    public bool GameStarted()
    {
        return this.gameStarted;
    }

    // Start is called before the first frame update
    void Start()
    {
        // Get the game server group for FleetIQ access
        Server.fleetIqGameServerGroup = "ExampleGameServerGroup";
        Console.WriteLine("My game server group: " + Server.fleetIqGameServerGroup);

        // Get my IP information through ipify
        var requestIPAddressPath = "https://api.ipify.org";
        StartCoroutine(GetMyIP(requestIPAddressPath));

        // Get my Task information to generate an identifier for this game server
        var ecsMetadataPath = Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI");
        Console.WriteLine("ECS metadata path: " + ecsMetadataPath);
        // Request the metadata
        StartCoroutine(GetTaskMetadata(ecsMetadataPath + "/task"));

        // Request instance-id for FleetIQ calls
        StartCoroutine(GetInstanceId("http://169.254.169.254/latest/meta-data/instance-id"));

        // Set FleetIQ update counter to trigger in a few seconds to inform that we're ready as soon as possible
        this.fleetIqUpdateCounter = Server.fleetIqUpdateInterval - 2.0f;
    }

    IEnumerator GetMyIP(string uri)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
        {
            // Request and wait for the desired page.
            yield return webRequest.SendWebRequest();

            if (webRequest.isNetworkError)
            {
                Debug.Log("Web request Error: " + webRequest.error);
            }
            else
            {
                Debug.Log("Web Request Received: " + webRequest.downloadHandler.text);
                // Set the public IP
                this.publicIP = webRequest.downloadHandler.text;
            }
        }
    }

    IEnumerator GetTaskMetadata(string uri)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
        {
            // Request and wait for the desired page.
            yield return webRequest.SendWebRequest();

            if (webRequest.isNetworkError)
            {
                Debug.Log("Task metadata Error: " + webRequest.error);
            }
            else
            {
                Debug.Log("Task metadata Received: " + webRequest.downloadHandler.text);
                var taskData = JsonUtility.FromJson<TaskData>(webRequest.downloadHandler.text);
                this.taskDataArn = taskData.TaskARN;
                // Search for the host port, a bit hacky but it's always 5 numbers after "HostPort":
                string afterHostport = webRequest.downloadHandler.text.Split(new string[] { "\"HostPort\":" }, StringSplitOptions.None)[1];
                Server.port = int.Parse(afterHostport.Substring(0, 5));
                Console.WriteLine("port: " + Server.port);
                this.taskDataArnWithContainer = taskData.TaskARN + "-" + Environment.GetEnvironmentVariable("CONTAINERNAME"); //Including the container name as we run multiple containers in a Task
                Debug.Log("TaskARN: " + this.taskDataArn);
                Debug.Log("TaskARN with container: " + this.taskDataArnWithContainer);
                // We will generate the game server name with the Task Arn id only to match the constraints
                var splittedArn = this.taskDataArnWithContainer.Split('/');
                this.gameServerId = splittedArn[splittedArn.Length - 1];
                Debug.Log("Game Server ID: " + this.gameServerId);
            }
        }
    }

    IEnumerator GetInstanceId(string uri)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
        {
            // Request and wait for the desired page.
            yield return webRequest.SendWebRequest();

            if (webRequest.isNetworkError)
            {
                Debug.Log("Web request Error: " + webRequest.error);
            }
            else
            {
                Debug.Log("Instance metadata Received: " + webRequest.downloadHandler.text);
                // Set the public IP
                this.instanceId = webRequest.downloadHandler.text;
            }
        }
    }

    private void Update()
    {
        // Create TCP server if we received port already
        if(Server.port > 0 && this.server == null)
        {
            Console.WriteLine("Port received, create network server");
            this.server = new NetworkServer(this);
        }

        // Register to FleetIQ if we have all the needed info and not registered yet
        if (!registeredToFleetIQ)
        {
            if (this.taskDataArnWithContainer != null && this.publicIP != null && this.instanceId != null)
            {
                Console.WriteLine("Registering to FleetIQ");
                var gameLiftConfig = new AmazonGameLiftConfig { RegionEndpoint = this.regionEndpoint };
                var gameLiftClient = new AmazonGameLiftClient(gameLiftConfig);
                var registerGameServerRequest = new RegisterGameServerRequest();
                registerGameServerRequest.GameServerGroupName = Server.fleetIqGameServerGroup;
                registerGameServerRequest.InstanceId = this.instanceId;
                registerGameServerRequest.ConnectionInfo = this.publicIP + ":" + Server.port;
                registerGameServerRequest.GameServerId = this.gameServerId;
                registerGameServerRequest.GameServerData = "{\"gametype\": \"normal\"}";
                var response = gameLiftClient.RegisterGameServerAsync(registerGameServerRequest);
                response.Wait();
                Console.WriteLine("Response HTTP code: " + response.Result.HttpStatusCode.ToString());
                Console.WriteLine("Response game server: " + response.Result.GameServer.GameServerData.ToString());
                Console.WriteLine("RegistrationRequest Sent!");

                this.registeredToFleetIQ = true;
             }
        }
    }

    // FixedUpdate is called 30 times per second (configured in Project Settings -> Time -> Fixed TimeStep).
    // This is the interval we're running the simulation and processing messages on the server
    void FixedUpdate()
    {
        // Don't run before Network server is created
        if (this.server == null)
            return;

        // Update the Network server to check client status and get messages
        server.Update();

        // Process any messages we received
        this.ProcessMessages();

        // Move players based on latest input and update player states to clients
        for (int i = 0; i < this.players.Count; i++)
        {
            var player = this.players[i];
            // Move
            player.Move();

            // Send state if changed
            var positionMessage = player.GetPositionMessage();
            if (positionMessage != null)
            {
                positionMessage.clientId = player.GetPlayerId();
                this.server.TransmitMessage(positionMessage, player.GetPlayerId());
                //Send to the player him/herself
                positionMessage.messageType = MessageType.PositionOwn;
                this.server.SendMessage(player.GetPlayerId(), positionMessage);
            }
        }

        // Update the server state to FleetIQ every 60. This will also get done when new clients connect
        this.fleetIqUpdateCounter += Time.fixedDeltaTime;
        if(this.fleetIqUpdateCounter > Server.fleetIqUpdateInterval && this.taskDataArnWithContainer != null)
        {
            this.UpdateFleetIQ();
            this.fleetIqUpdateCounter = 0.0f;
        }

        // If a new player joined, update FleetIQ as well
        if(this.server.GetPlayerCount() > this.lastPlayerCount)
        {
            Debug.Log("New player joined, update FleetIQ");
            this.UpdateFleetIQ();
            this.lastPlayerCount = this.server.GetPlayerCount();
        }
    }

    private void ProcessMessages()
    {
        // Go through any messages we received to process
        foreach (SimpleMessage msg in messagesToProcess)
        {
            // Spawn player
            if (msg.messageType == MessageType.Spawn)
            {
                Debug.Log("Player spawned: " + msg.float1 + "," + msg.float2 + "," + msg.float3);
                NetworkPlayer player = new NetworkPlayer(msg.clientId);
                this.players.Add(player);
                player.Spawn(msg, this.playerPrefab);
                player.SetPlayerId(msg.clientId);

                // Send all existing player positions to the newly joined
                for (int i = 0; i < this.players.Count-1; i++)
                {
                    var otherPlayer = this.players[i];
                    // Send state
                    var positionMessage = otherPlayer.GetPositionMessage(overrideChangedCheck: true);
                    if (positionMessage != null)
                    {
                        positionMessage.clientId = otherPlayer.GetPlayerId();
                        this.server.SendMessage(player.GetPlayerId(), positionMessage);
                    }
                }
            }

            // Set player input
            if (msg.messageType == MessageType.PlayerInput)
            {
                // Only handle input if the player exists
                if (this.PlayerExists(msg.clientId))
                {
                    Debug.Log("Player moved: " + msg.float1 + "," + msg.float2 + " ID: " + msg.clientId);

                    if (this.PlayerExists(msg.clientId))
                    {
                        var player = this.GetPlayer(msg.clientId);
                        player.SetInput(msg);
                    }
                    else
                    {
                        Debug.Log("PLAYER MOVED BUT IS NOT SPAWNED! SPAWN TO RANDOM POS");
                        Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-5, 5), 1, UnityEngine.Random.Range(-5, 5));
                        var quat = Quaternion.identity;
                        SimpleMessage tmpMsg = new SimpleMessage(MessageType.Spawn);
                        tmpMsg.SetFloats(spawnPos.x, spawnPos.y, spawnPos.z, quat.x, quat.y, quat.z, quat.w);
                        tmpMsg.clientId = msg.clientId;

                        NetworkPlayer player = new NetworkPlayer(msg.clientId);
                        this.players.Add(player);
                        player.Spawn(tmpMsg, this.playerPrefab);
                        player.SetPlayerId(msg.clientId);
                    }
                }
                else
                {
                    Debug.Log("Player doesn't exists anymore, don't take in input: " + msg.clientId);
                }
            }
        }
        messagesToProcess.Clear();
    }

    public void DisconnectAll()
    {
        this.server.DisconnectAll();
    }

    public void UpdateFleetIQ(bool serverTerminated = false)
    {
        // Only update if we're ready
        if(this.server.IsReady())
        {
            Console.WriteLine("Server Ready, updating to FleetIQ");

            var utilizationStatus = GameServerUtilizationStatus.AVAILABLE;

            // If we have a player connected, we're utilized (backend has claimed this game server for 2 players)
            if (this.server.GetPlayerCount() > 0)
            {
                utilizationStatus = GameServerUtilizationStatus.UTILIZED;
            }

            var gameLiftConfig = new AmazonGameLiftConfig { RegionEndpoint = this.regionEndpoint };
            var gameLiftClient = new AmazonGameLiftClient(gameLiftConfig);
            var updateGameServerRequest = new UpdateGameServerRequest();
            updateGameServerRequest.GameServerGroupName = Server.fleetIqGameServerGroup;
            updateGameServerRequest.GameServerId = this.gameServerId;
            updateGameServerRequest.HealthCheck = GameServerHealthCheck.HEALTHY;
            updateGameServerRequest.UtilizationStatus = utilizationStatus;

            gameLiftClient.UpdateGameServerAsync(updateGameServerRequest);

            Console.WriteLine("FleetIQ Updat sent!");
        }
    }

}

// *** SERVER NETWORK LOGIC *** //

public class NetworkServer
{
	private TcpListener listener;
    // Clients are stored as a dictionary of the TCPCLient and the ClientID
    private Dictionary<TcpClient, int> clients = new Dictionary<TcpClient,int>();
    private List<TcpClient> readyClients = new List<TcpClient>();
    private List<TcpClient> clientsToRemove = new List<TcpClient>();

    private Server server = null;

    private bool ready = false;

    public int GetPlayerCount() { return clients.Count; }
    public bool IsReady() { return this.ready; }

    // Ends the game session for all and disconnects the players
    public void TerminateGameSession()
    {
        Console.WriteLine("Terminating session and Task");
        // Let FleetIQ know we are done
        var gameLiftConfig = new AmazonGameLiftConfig { RegionEndpoint = this.server.regionEndpoint };
        var gameLiftClient = new AmazonGameLiftClient(gameLiftConfig);
        var deregisterGameServerRequest = new DeregisterGameServerRequest();
        deregisterGameServerRequest.GameServerGroupName = Server.fleetIqGameServerGroup;
        deregisterGameServerRequest.GameServerId = this.server.GetGameServerId();
        var response = gameLiftClient.DeregisterGameServerAsync(deregisterGameServerRequest);
        response.Wait();
        Console.WriteLine("Deregistered from FleetIQ, terminate Task...");
        Application.Quit();
    }

    public NetworkServer(Server server)
	{
        this.server = server;

        //Start the TCP server
        int port = Server.port;
        Debug.Log("Starting server on port " + port);
        listener = new TcpListener(IPAddress.Any, 1935); //We listen to 1935 on all servers, Bridge networking will map this to a dynamic port
        Debug.Log("Listening at: " + listener.LocalEndpoint.ToString());
		listener.Start();
        this.ready = true;
	}

    // Checks if socket is still connected
    private bool IsSocketConnected(TcpClient client)
    {
        var bClosed = false;

        // Detect if client disconnected
        if (client.Client.Poll(0, SelectMode.SelectRead))
        {
            byte[] buff = new byte[1];
            if (client.Client.Receive(buff, SocketFlags.Peek) == 0)
            {
                // Client disconnected
                bClosed = true;
            }
        }

        return !bClosed;
    }

    public void Update()
	{
		// Are there any new connections pending?
		if (listener.Pending())
		{
            System.Console.WriteLine("Client pending..");
			TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true; // Use No Delay to send small messages immediately. UDP should be used for even faster messaging
            System.Console.WriteLine("Client accepted.");

            // We have a maximum of 2 clients per game
            if(this.clients.Count < Server.maxPlayers)
            {
                // Add client and give it the Id of the value of rollingPlayerId
                this.clients.Add(client, this.server.rollingPlayerId);
                this.server.rollingPlayerId++;
                return;
            }
            else
            {
                // game already full, reject the connection
                try
                {
                    SimpleMessage message = new SimpleMessage(MessageType.Reject, "game already full");
                    NetworkProtocol.Send(client, message);
                }
                catch (SocketException) { }
            }

		}

        // Iterate through clients and check if they have new messages or are disconnected
        int playerIdx = 0;
        foreach (var client in this.clients)
		{
            var tcpClient = client.Key;
            try
            {
                if (tcpClient == null) continue;
                if (this.IsSocketConnected(tcpClient) == false)
                {
                    System.Console.WriteLine("Client not connected anymore");
                    this.clientsToRemove.Add(tcpClient);
                }
                var messages = NetworkProtocol.Receive(tcpClient);
                foreach(SimpleMessage message in messages)
                {
                    System.Console.WriteLine("Received message: " + message.message + " type: " + message.messageType);
                    bool disconnect = HandleMessage(playerIdx, tcpClient, message);
                    if (disconnect)
                        this.clientsToRemove.Add(tcpClient);
                }
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Error receiving from a client: " + e.Message);
                this.clientsToRemove.Add(tcpClient);
            }
            playerIdx++;
		}

        //Remove dead clients
        foreach (var clientToRemove in this.clientsToRemove)
        {
            try
            {
                this.RemoveClient(clientToRemove);
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Couldn't remove client: " + e.Message);
            }
        }
        this.clientsToRemove.Clear();

        //End game if no clients
        if(this.server.GameStarted())
        {
            if(this.clients.Count <= 0)
            {
                System.Console.WriteLine("Clients gone, stop session");
                this.TerminateGameSession();
            }
        }
    }

    public void DisconnectAll()
    {
        // warn clients
        SimpleMessage message = new SimpleMessage(MessageType.Disconnect);
        TransmitMessage(message);
        // disconnect connections
        foreach (var client in this.clients)
        {
            this.clientsToRemove.Add(client.Key);
        }

        //Reset the client lists
        this.clients = new Dictionary<TcpClient, int>();
        this.readyClients = new List<TcpClient>();
        this.server.players = new List<NetworkPlayer>();
	}

    public void TransmitMessage(SimpleMessage msg, int excludeClient)
    {
        // send the same message to all players
        foreach (var client in this.clients)
        {
            //Skip if this is the excluded client
            if (client.Value == excludeClient)
            {
                continue;
            }

            try
            {
                NetworkProtocol.Send(client.Key, msg);
            }
            catch (Exception e)
            {
                this.clientsToRemove.Add(client.Key);
            }
        }
    }

    //Transmit message to multiple clients
	public void TransmitMessage(SimpleMessage msg, TcpClient excludeClient = null)
	{
        // send the same message to all players
        foreach (var client in this.clients)
		{
            //Skip if this is the excluded client
            if(excludeClient != null && excludeClient == client.Key)
            {
                continue;
            }

			try
			{
				NetworkProtocol.Send(client.Key, msg);
			}
			catch (Exception e)
			{
                this.clientsToRemove.Add(client.Key);
			}
		}
    }

    private TcpClient SearchClient(int clientId)
    {
        foreach(var client in this.clients)
        {
            if(client.Value == clientId)
            {
                return client.Key;
            }
        }
        return null;
    }

    public void SendMessage(int clientId, SimpleMessage msg)
    {
        try
        {
            TcpClient client = this.SearchClient(clientId);
            SendMessage(client, msg);
        }
        catch (Exception e)
        {
            Console.WriteLine("Failed to send message to client: " + clientId);
        }
    }
    //Send message to single client
    private void SendMessage(TcpClient client, SimpleMessage msg)
    {
        try
        {
            NetworkProtocol.Send(client, msg);
        }
        catch (Exception e)
        {
            this.clientsToRemove.Add(client);
        }
    }

    private bool HandleMessage(int playerIdx, TcpClient client, SimpleMessage msg)
	{
        if (msg.messageType == MessageType.Disconnect)
        {
            this.clientsToRemove.Add(client);
            return true;
        }
        else if (msg.messageType == MessageType.Ready)
            HandleReady(client);
        else if (msg.messageType == MessageType.Spawn)
            HandleSpawn(client, msg);
        else if (msg.messageType == MessageType.PlayerInput)
            HandleMove(client, msg);

        return false;
    }

	private void HandleReady(TcpClient client)
	{
        // start the game once we have at least one client online
        this.readyClients.Add(client);

        if (readyClients.Count >= 1)
        {
            System.Console.WriteLine("Enough clients, let's start the game!");
            this.server.StartGame();
        }
	}

    private void HandleSpawn(TcpClient client, SimpleMessage message)
    {
        // Get client id (this is the value in the dictionary where the TCPClient is the key)
        int clientId = this.clients[client];

        System.Console.WriteLine("Player " + clientId + " spawned with coordinates: " + message.float1 + "," + message.float2 + "," + message.float3);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the server
        Server.messagesToProcess.Add(message);
    }

    private void HandleMove(TcpClient client, SimpleMessage message)
    {
        // Get client id (this is the value in the dictionary where the TCPClient is the key)
        int clientId = this.clients[client];

        System.Console.WriteLine("Got move from client: " + clientId + " with input: " + message.float1 + "," + message.float2);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the server
        Server.messagesToProcess.Add(message);
    }

    private void RemoveClient(TcpClient client)
    {
        //Let the other clients know the player was removed
        int clientId = this.clients[client];

        SimpleMessage message = new SimpleMessage(MessageType.PlayerLeft);
        message.clientId = clientId;
        TransmitMessage(message, client);

        // Disconnect and remove
        this.DisconnectPlayer(client);
        this.clients.Remove(client);
        this.readyClients.Remove(client);
        this.server.RemovePlayer(clientId);
    }

	private void DisconnectPlayer(TcpClient client)
	{
        try
        {
            // remove the client and close the connection
            if (client != null)
            {
                NetworkStream stream = client.GetStream();
                stream.Close();
                client.Close();
            }
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Failed to disconnect player: " + e.Message);
        }
	}
#endif
}
