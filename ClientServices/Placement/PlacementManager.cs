﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientInterfaces.Collision;
using ClientInterfaces.GOC;
using ClientInterfaces.Map;
using ClientInterfaces.Network;
using ClientInterfaces.Placement;
using ClientInterfaces.Player;
using ClientInterfaces.Resource;
using ClientWindow;
using GorgonLibrary;
using GorgonLibrary.Graphics;
using Lidgren.Network;
using SS13_Shared;
using System.Drawing;
using ClientInterfaces;
using CGO;
using SS13_Shared.GO;
using SS13.IoC;

using ClientInterfaces.UserInterface;
using ClientInterfaces.Map;
using ClientServices.Map;
using SS13.IoC;

namespace ClientServices.Placement
{
    public class PlacementManager : IPlacementManager
    {
        public readonly IResourceManager ResourceManager;
        public readonly INetworkManager NetworkManager;
        public readonly ICollisionManager CollisionManager;
        public readonly IPlayerManager PlayerManager;

        public Sprite CurrentBaseSprite;
        public EntityTemplate CurrentTemplate;
        public PlacementInformation CurrentPermission;
        public PlacementMode CurrentMode;
        public Boolean ValidPosition;

        public Boolean IsActive { get; private set; }
        public Boolean Eraser { get; private set; }

        public Direction Direction = Direction.South;

        public event EventHandler PlacementCanceled;

        private readonly Dictionary<string, Type> _modeDictionary = new Dictionary<string, Type>(); 

        public PlacementManager(IResourceManager resourceManager, INetworkManager networkManager, ICollisionManager collisionManager, IPlayerManager playerManager)
        {
            ResourceManager = resourceManager;
            NetworkManager = networkManager;
            CollisionManager = collisionManager;
            PlayerManager = playerManager;

            Type type = typeof(PlacementMode);
            List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            List<Type> types = assemblies.SelectMany(t => t.GetTypes()).Where(p => type.IsAssignableFrom(p)).ToList();

            _modeDictionary.Clear();
            foreach (Type t in types)
                _modeDictionary.Add(t.Name, t);

            Clear();
        }

        public void HandleNetMessage(NetIncomingMessage msg)
        {
            var messageType = (PlacementManagerMessage)msg.ReadByte();

            switch (messageType)
            {
                case PlacementManagerMessage.StartPlacement:
                    HandleStartPlacement(msg);
                    break;
                case PlacementManagerMessage.CancelPlacement:
                    Clear();
                    break;
                case PlacementManagerMessage.PlacementFailed:
                    //Sad trombone here.
                    break;
            }
        }

        private void HandleStartPlacement(NetIncomingMessage msg)
        {
            CurrentPermission = new PlacementInformation
                                     {
                                         Range = msg.ReadUInt16(),
                                         IsTile = msg.ReadBoolean()
                                     };

            var mapMgr = (MapManager)IoCManager.Resolve<IMapManager>();

            if (CurrentPermission.IsTile) CurrentPermission.TileType = mapMgr.GetTileString(msg.ReadByte());
            else CurrentPermission.EntityType = msg.ReadString();
            CurrentPermission.PlacementOption = msg.ReadString();

            BeginPlacing(CurrentPermission);
        }

        public void Clear()
        {
            CurrentBaseSprite = null;
            CurrentTemplate = null;
            CurrentPermission = null;
            CurrentMode = null;
            if (PlacementCanceled != null && IsActive && !Eraser) PlacementCanceled(this, null);
            IsActive = false;
            Eraser = false;
        }

        public void Rotate()
        {
            switch (Direction)
            {
                case Direction.North:
                    Direction = Direction.East;
                    break;
                case Direction.East:
                    Direction = Direction.South;
                    break;
                case Direction.South:
                    Direction = Direction.West;
                    break;
                case Direction.West:
                    Direction = Direction.North;
                    break;
            }
        }

        public void HandlePlacement()
        {
            if (IsActive && !Eraser)
                RequestPlacement();
        }

        public void HandleDeletion(IEntity entity)
        {
            if (!IsActive || !Eraser) return;

            var message = NetworkManager.CreateMessage();
            message.Write((byte)NetMessage.RequestEntityDeletion);
            message.Write(entity.Uid);
            NetworkManager.SendMessage(message, NetDeliveryMethod.ReliableUnordered);
        }

        public void ToggleEraser()
        {
            if (!Eraser && !IsActive)
            {
                IsActive = true;
                Eraser = true;
            }
            else Clear();
        }

        public void BeginPlacing(PlacementInformation info)
        {
            Clear();

            IoCManager.Resolve<IUserInterfaceManager>().CancelTargeting();
            IoCManager.Resolve<IUserInterfaceManager>().DragInfo.Reset();

            CurrentPermission = info;

            if (!_modeDictionary.Any(pair => pair.Key.Equals(CurrentPermission.PlacementOption)))
            {
                Clear();
                return;
            }

            var modeType = _modeDictionary.First(pair => pair.Key.Equals(CurrentPermission.PlacementOption)).Value;
            CurrentMode = (PlacementMode)Activator.CreateInstance(modeType, this);

            if (info.IsTile)
                PreparePlacementTile(info.TileType);
            else
                PreparePlacement(info.EntityType);
        }

        private void PreparePlacement(string templateName)
        {
            var template = EntityManager.Singleton.TemplateDb.GetTemplate(templateName);
            if (template == null) return;

            var spriteParam = template.GetBaseSpriteParamaters().FirstOrDefault(); //Will break if states not ordered correctly.
            if (spriteParam == null) return;

            var spriteName = spriteParam.GetValue<string>();
            var sprite = ResourceManager.GetSprite(spriteName);

            CurrentBaseSprite = sprite;
            CurrentTemplate = template;

            IsActive = true;
        }

        private void PreparePlacementTile(string tileType)
        {
            CurrentBaseSprite = ResourceManager.GetSprite("tilebuildoverlay");

            IsActive = true;
        }

        private void RequestPlacement() //
        {
            if (CurrentPermission == null) return;
            if (!ValidPosition) return;

            var mapMgr = (MapManager)IoCManager.Resolve<IMapManager>();
            NetOutgoingMessage message = NetworkManager.CreateMessage();

            message.Write((byte)NetMessage.PlacementManagerMessage);
            message.Write((byte)PlacementManagerMessage.RequestPlacement);
            message.Write("AlignNone"); //Temporarily disable sanity checks.

            message.Write(CurrentPermission.IsTile);

            if (CurrentPermission.IsTile) message.Write(mapMgr.GetTileIndex(CurrentPermission.TileType));
            else message.Write(CurrentPermission.EntityType);

            message.Write(CurrentMode.mouseWorld.X);
            message.Write(CurrentMode.mouseWorld.Y);

            message.Write((byte)Direction);

            message.Write(CurrentMode.currentTile.TilePosition.X);
            message.Write(CurrentMode.currentTile.TilePosition.Y);

            NetworkManager.SendMessage(message, NetDeliveryMethod.ReliableUnordered);
        }

        public Sprite GetDirectionalSprite()
        {
            Sprite spriteToUse = CurrentBaseSprite;

            if (CurrentBaseSprite == null) return null;

            string dirName = (CurrentBaseSprite.Name + "_" + Direction.ToString()).ToLowerInvariant();
            if (ResourceManager.SpriteExists(dirName))
                spriteToUse = ResourceManager.GetSprite(dirName);

            return spriteToUse;
        }

        public void Update(Vector2D mouseScreen, IMapManager currentMap)
        {
            if (currentMap == null || CurrentPermission == null || CurrentMode == null) return;

            ValidPosition = CurrentMode.Update(mouseScreen, currentMap);
        }

        public void Render()
        {
            if (CurrentMode != null)
                CurrentMode.Render();

            if (CurrentPermission != null && CurrentPermission.Range > 0)
            {
                Gorgon.CurrentRenderTarget.Circle(
                    PlayerManager.ControlledEntity.Position.X - ClientWindowData.Singleton.ScreenOrigin.X,
                    PlayerManager.ControlledEntity.Position.Y - ClientWindowData.Singleton.ScreenOrigin.Y,
                    CurrentPermission.Range,
                    Color.White,
                    new Vector2D(2, 2));
            }
        }
            
    }
}
