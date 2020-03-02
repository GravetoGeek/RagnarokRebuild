﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Leopotam.Ecs;
using RebuildData.Server.Logging;
using RebuildData.Shared.Data;
using RebuildData.Shared.Enum;
using RebuildZoneServer.Data;
using RebuildZoneServer.Data.Management;
using RebuildZoneServer.EntityComponents;
using RebuildZoneServer.EntitySystems;
using RebuildZoneServer.Networking;
using RebuildZoneServer.Util;

namespace RebuildZoneServer.Sim
{
	public class World
	{
		private readonly List<Map> Maps = new List<Map>();

		private Dictionary<int, EcsEntity> entityList = new Dictionary<int, EcsEntity>();
		private Dictionary<string, int> mapIdLookup = new Dictionary<string, int>();

		private EcsWorld ecsWorld;
		private EcsSystems ecsSystems;

		private int nextEntityId = 0;
		private int maxEntityId = 10_000_000;

		const int mapCount = 2;
		const int entityCount = 600;

		public World()
		{
			var initialMaxEntities = NextPowerOf2(mapCount * entityCount);
			if (initialMaxEntities < 1024)
				initialMaxEntities = 1024;

			ecsWorld = new EcsWorld(initialMaxEntities);

			ecsSystems = new EcsSystems(ecsWorld)
				.Inject(this)
				.Add(new MonsterSystem())
				.Add(new CharacterSystem())
				.Add(new PlayerSystem());

			ecsSystems.Init();

			var maps = DataManager.Maps;

			var entities = 0;

			for (var j = 0; j < maps.Count; j++)
			{
				var mapData = maps[j];
				try
				{
					var map = new Map(this, mapData.Code, mapData.WalkData);
					map.Id = j;

					mapIdLookup.Add(mapData.Code, j);

					var spawns = DataManager.GetSpawnsForMap(mapData.Code);

					if (spawns != null)
					{
						for (var i = 0; i < spawns.Count; i++)
						{
							var s = spawns[i];
							var mobId = DataManager.GetMonsterIdForCode(s.Class);

							for (var k = 0; k < s.Count; k++)
							{
								var m = CreateMonster(map, mobId, s.X, s.Y, s.Width, s.Height);
								if (!m.IsNull())
								{
									map.AddEntity(ref m);
									entities++;
								}
							}
						}
					}

					var connectors = DataManager.GetMapConnectors(mapData.Code);

					if (connectors != null)
					{
						for (var i = 0; i < connectors.Count; i++)
						{
							var c = connectors[i];
							var mobId = 1000;

							var m = CreateMonster(map, mobId, c.SrcArea.MidX, c.SrcArea.MidY, 0, 0);
							if (!m.IsNull())
								map.AddEntity(ref m);
						}
					}

					Maps.Add(map);
				}
				catch (Exception e)
				{
					ServerLogger.LogError($"Failed to load map {mapData.Name} ({mapData.Code}) due to error while loading: {e.Message}");
				}
			}
				

			ServerLogger.Log($"World started with {entities} entities.");
		}

		public void Update()
		{
			ecsSystems.Run();
			for (var i = 0; i < mapCount; i++)
				Maps[i].Update();

			if (CommandBuilder.HasRecipients())
				ServerLogger.LogWarning("Command builder has recipients after completing server update loop!");
		}

		public EcsEntity CreateMonster(Map map, int classId, int x, int y, int width, int height)
		{
			var e = ecsWorld.AddAndReset<Character, CombatEntity, Monster>(
				out var ch, out var ce, out var m);

			var area = Area.CreateAroundPoint(new Position(x, y), width, height);
			area.ClipArea(map.MapBounds);

			Position p;
			if (width == 0 && height == 0 && x != 0 && y != 0)
			{
				p = new Position(x, y);
			}
			else
			{
				if (x == 0 && y == 0 && width == 0 && height == 0)
					area = map.MapBounds;

				if (!map.FindPositionInRange(area, out p))
				{
					ServerLogger.LogWarning($"Failed to spawn {classId} on map {map.Name}, could not find spawn location around {x},{y}. Spawning randomly on map.");
					map.FindPositionInRange(map.MapBounds, out p);
				}
			}

			var mon = DataManager.GetMonsterById(classId);
			ch.Id = GetNextEntityId();
			ch.ClassId = classId;
			ch.Entity = e;
			ch.Position = p;
			ch.MoveSpeed = mon.MoveSpeed;
			ch.Type = CharacterType.Monster;
			ch.FacingDirection = (Direction)GameRandom.Next(0, 7);

			//ServerLogger.Log("Entity spawned at position: " + ch.Position);

			return e;
		}

		public EcsEntity CreatePlayer(NetworkConnection connection, string mapName, Area spawnArea)
		{
			var e = ecsWorld.AddAndReset<Character, CombatEntity, Player>(
				out var ch, out var ce, out var player);

			var mapId = mapIdLookup[mapName];

			var map = Maps[mapId];

			if (spawnArea.IsZero)
				spawnArea = map.MapBounds;

			Position p;

			if (spawnArea.Width > 1 || spawnArea.Height > 1)
			{
				p = spawnArea.RandomInArea();

				//Position p = new Position(170 + GameRandom.Next(-5, 5), 365 + GameRandom.Next(-5, 5));
				var attempt = 0;
				do
				{
					attempt++;
					if (attempt > 100)
					{
						ServerLogger.LogWarning("Trouble spawning player, will place him on a random cell instead.");
						spawnArea = map.MapBounds;
						attempt = 0;
					}

					p = spawnArea.RandomInArea();
				} while (!map.WalkData.IsCellWalkable(p));
			}
			else
				p = new Position(spawnArea.MidX, spawnArea.MidY);

			ch.Id = GetNextEntityId();
			ch.IsActive = false; //start off inactive

			ch.Entity = e;
			ch.Position = p;
			ch.ClassId = GameRandom.Next(0, 6);
			ch.MoveSpeed = 0.15f;
			ch.Type = CharacterType.Player;
			ch.FacingDirection = (Direction)GameRandom.Next(0, 7);

			player.Connection = connection;
			player.Entity = e;
			player.IsMale = GameRandom.Next(0, 1) == 0;
			player.HeadId = (byte)GameRandom.Next(0, 28);
			//player.IsMale = false;

			//player.IsMale = true;
			//ch.ClassId = 1;
			//player.HeadId = 15;

			//map.SendAllEntitiesToPlayer(ref e);
			map.AddEntity(ref e);


			return e;
		}
		
		public void RemoveEntity(ref EcsEntity entity)
		{
			ServerLogger.Log($"Removing entity {entity} from world.");
			var player = entity.Get<Player>();
			var combatant = entity.Get<CombatEntity>();
			var monster = entity.Get<Monster>();

			var ch = entity.Get<Character>();
			if (ch != null)
				entityList.Remove(ch.Id);

			player?.Reset();
			combatant?.Reset();
			ch?.Reset();
			monster?.Reset();

			entity.Destroy();
		}

		public void MovePlayerMap(ref EcsEntity entity, Character character, string mapName, Position newPosition)
		{
			character.IsActive = false;
			character.Map.RemoveEntity(ref entity);

			character.ResetState();
			character.Position = newPosition;

			if (!mapIdLookup.TryGetValue(mapName, out var mapId))
			{
				ServerLogger.LogWarning($"Map {mapName} does not exist! Could not move player.");
				return;
			}

			var map = Maps[mapId];


			if (newPosition == Position.Zero)
			{
				map.FindPositionInRange(map.MapBounds, out var p);
				character.Position = newPosition;
			}


			map.AddEntity(ref entity);

			var player = entity.Get<Player>();
			player.Connection.LastKeepAlive = Time.ElapsedTime; //reset tick time so they get 2 mins to load the map

			CommandBuilder.SendChangeMap(character, player);
		}

		private int NextPowerOf2(int n)
		{
			n--;
			n |= n >> 1;
			n |= n >> 2;
			n |= n >> 4;
			n |= n >> 8;
			n |= n >> 16;
			n++;
			return n;
		}

		private int GetNextEntityId()
		{
			nextEntityId++;
			if (nextEntityId > maxEntityId)
				nextEntityId = 0;

			if (!entityList.ContainsKey(nextEntityId))
				return nextEntityId;

			while (entityList.ContainsKey(nextEntityId))
				nextEntityId++;

			return nextEntityId;
		}
	}
}