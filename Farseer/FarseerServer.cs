using Vintagestory.API.Server;
using Vintagestory.API.Common;
using System;
using System.Collections.Generic;

namespace Farseer;

public class FarseerServer : IDisposable
{
    public class FarseePlayer
    {
        public int FarViewDistance { get; set; }
    }

    ModSystem modSystem;
    ICoreServerAPI sapi;

    FarRegionAccess regionAccess;
    Dictionary<IServerPlayer, FarseePlayer> players = new Dictionary<IServerPlayer, FarseePlayer>();

    public FarseerServer(ModSystem mod, ICoreServerAPI sapi)
    {
        this.modSystem = mod;
        this.sapi = sapi;
        this.regionAccess = new FarRegionAccess(modSystem, sapi);

        // this.map = new FarChunkMap();
        // sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        // map.NewChunkLoaded += (coord, chunk) =>
        // {
        //     channel.BroadcastPacket(new FarChunkMessage { ChunkPosX = coord.X, ChunkPosZ = coord.Y, Heightmap = chunk.Heightmap });
        // };
        //
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
        sapi.Event.RegisterGameTickListener(OnGameTick, 1000);

        var channel = sapi.Network.GetChannel(FarseerModSystem.MOD_CHANNEL_NAME);
        channel.SetMessageHandler<FarEnableRequest>(EnableFarseeForPlayer);
    }

    private void OnGameTick(float time)
    {
        //modSystem.Mod.Logger.Chat(time.ToString());
    }

    private void EnableFarseeForPlayer(IServerPlayer fromPlayer, FarEnableRequest request)
    {
        if (players.TryGetValue(fromPlayer, out FarseePlayer player))
        {
            // Just update the desired render distance.
            // TODO: Send more if its larger i guess
            player.FarViewDistance = 2048;
        }
        else
        {
            players.Add(fromPlayer, new FarseePlayer() { FarViewDistance = request.FarViewDistance = 2048 });
        }
        modSystem.Mod.Logger.Chat("enabled for player " + fromPlayer.PlayerName);


        var channel = sapi.Network.GetChannel(FarseerModSystem.MOD_CHANNEL_NAME);

        var playerBlockPos = fromPlayer.Entity.Pos.AsBlockPos;
        var playerRegionIdx = sapi.WorldManager.MapRegionIndex2DByBlockPos(playerBlockPos.X, playerBlockPos.Z);

        var playerRegionCoord = sapi.WorldManager.MapRegionPosFromIndex2D(playerRegionIdx);

        modSystem.Mod.Logger.Chat("playerRegionIdx: {0}, playerRegionCoord: {1}", playerRegionIdx, playerRegionCoord);

        int farViewDistanceInRegions = request.FarViewDistance / sapi.WorldManager.RegionSize;

        for (var x = -farViewDistanceInRegions; x <= farViewDistanceInRegions; x++)
        {
            for (var z = -farViewDistanceInRegions; z <= farViewDistanceInRegions; z++)
            {
                var thisRegionX = playerRegionCoord.X + x;
                var thisRegionZ = playerRegionCoord.Z + z;
                var thisIdx = sapi.WorldManager.MapRegionIndex2D(thisRegionX, thisRegionZ);
                var regionData = regionAccess.GetDummyData(thisIdx);

                channel.SendPacket(regionData, fromPlayer);
            }
        }
    }

    private void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        if (players.ContainsKey(byPlayer))
        {
            players.Remove(byPlayer);
        }
    }


    public void Dispose()
    {
    }
}
