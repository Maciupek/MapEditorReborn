﻿// -----------------------------------------------------------------------
// <copyright file="SchematicObject.cs" company="MapEditorReborn">
// Copyright (c) MapEditorReborn. All rights reserved.
// Licensed under the CC BY-SA 3.0 license.
// </copyright>
// -----------------------------------------------------------------------

namespace MapEditorReborn.API.Features.Objects
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Collections.Specialized;
    using System.IO;
    using System.Linq;
    using AdminToys;
    using Enums;
    using Exiled.API.Enums;
    using Exiled.API.Features;
    using Exiled.API.Features.Items;
    using Extensions;
    using MEC;
    using Mirror;
    using Serializable;
    using UnityEngine;
    using Utf8Json;

    using Object = UnityEngine.Object;

    /// <summary>
    /// Component added to SchematicObject. Is is used for easier idendification of the object and it's variables.
    /// </summary>
    public class SchematicObject : MapEditorObject
    {
        /// <summary>
        /// Initializes the <see cref="SchematicObject"/>.
        /// </summary>
        /// <param name="schematicSerializable">The <see cref="SchematicSerializable"/> to instantiate.</param>
        /// <param name="data">The object data from a file.</param>
        /// <returns>Instance of this compoment.</returns>
        public SchematicObject Init(SchematicSerializable schematicSerializable, SchematicObjectDataList data)
        {
            Base = schematicSerializable;
            SchematicData = data;
            DirectoryPath = data.Path;
            ForcedRoomType = schematicSerializable.RoomType != RoomType.Unknown ? schematicSerializable.RoomType : FindRoom().Type;

            ObjectFromId = new Dictionary<int, Transform>(SchematicData.Blocks.Count + 1)
            {
                { data.RootObjectId, transform },
            };
            CreateRecursiveFromID(data.RootObjectId, data.Blocks, transform);
            CreateTeleporters();
            AddRigidbodies();
            IsBuilt = true;

            AttachedBlocks.CollectionChanged += OnCollectionChanged;
            UpdateObject();

            Timing.CallDelayed(1f, () => AssetBundle.UnloadAllAssetBundles(false));
            return this;
        }

        /// <summary>
        /// The base config of the object which contains its properties.
        /// </summary>
        public SchematicSerializable Base;

        /// <summary>
        /// Gets a <see cref="SchematicObjectDataList"/> used to build a schematic.
        /// </summary>
        public SchematicObjectDataList SchematicData { get; private set; }

        /// <summary>
        /// Gets a schematic directory path.
        /// </summary>
        public string DirectoryPath { get; private set; }

        /// <summary>
        /// Gets a <see cref="List{T}"/> of <see cref="GameObject"/> which contains all attached blocks.
        /// </summary>
        public ObservableCollection<GameObject> AttachedBlocks { get; private set; } = new ObservableCollection<GameObject>();

        /// <summary>
        /// Gets the original position.
        /// </summary>
        public Vector3 OriginalPosition { get; private set; }

        /// <summary>
        /// Gets the original rotation.
        /// </summary>
        public Vector3 OriginalRotation { get; private set; }

        /// <summary>
        /// Gets the schematic name.
        /// </summary>
        public string Name => Base.SchematicName;

        public AnimationController AnimationController => AnimationController.Get(this);

        public bool IsRootSchematic => transform.root == transform;

        /// <summary>
        /// Gets the read-only collections of <see cref="NetworkIdentity"/> in this schematic.
        /// </summary>
        public ReadOnlyCollection<NetworkIdentity> NetworkIdentities
        {
            get
            {
                if (_networkIdentities == null)
                {
                    List<NetworkIdentity> list = new();

                    foreach (GameObject gameObject in AttachedBlocks)
                    {
                        if (gameObject.TryGetComponent(out NetworkIdentity networkIdentity))
                        {
                            list.Add(networkIdentity);
                        }
                    }

                    _networkIdentities = list.AsReadOnly();
                }

                return _networkIdentities;
            }
        }

        /// <inheritdoc cref="MapEditorObject.UpdateObject()"/>
        public override void UpdateObject()
        {
            if (IsRootSchematic && Base.SchematicName != name.Split(new[] { '-' })[1])
            {
                SchematicObject newObject = ObjectSpawner.SpawnSchematic(Base, transform.position, transform.rotation, transform.localScale);

                if (newObject != null)
                {
                    API.SpawnedObjects[API.SpawnedObjects.IndexOf(this)] = newObject;

                    Destroy();
                    return;
                }

                Base.SchematicName = name.Replace("CustomSchematic-", string.Empty);
            }

            OriginalPosition = RelativePosition;
            OriginalRotation = RelativeRotation;

            foreach (GameObject gameObject in AttachedBlocks)
            {
                if (gameObject.TryGetComponent(out InventorySystem.Items.Firearms.Attachments.WorkstationController _))
                {
                    NetworkServer.UnSpawn(gameObject);

                    SchematicBlockData block = SchematicData.Blocks.Find(c => c.ObjectId == _workstationsTransformProperties[gameObject.transform.GetInstanceID()]);
                    gameObject.transform.position = transform.position + block.Position;
                    gameObject.transform.eulerAngles = transform.eulerAngles + block.Rotation;
                    gameObject.transform.localScale = Vector3.Scale(transform.localScale, block.Scale);

                    NetworkServer.Spawn(gameObject);
                }
            }

            if (!IsRootSchematic)
                return;

            Timing.CallDelayed(0.1f, () => Patches.OverridePositionPatch.ResetValues());
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => _networkIdentities = null;

        private void CreateRecursiveFromID(int id, List<SchematicBlockData> blocks, Transform parentGameObject)
        {
            Transform childGameObjectTransform = CreateObject(blocks.Find(c => c.ObjectId == id), parentGameObject) ?? transform; // Create the object first before creating children.
            int[] parentSchematics = blocks.Where(bl => bl.BlockType == BlockType.Schematic).Select(bl => bl.ObjectId).ToArray();

            // Gets all the ObjectIds of all the schematic blocks inside "blocks" argument.
            foreach (SchematicBlockData block in blocks.FindAll(c => c.ParentId == id))
            {
                if (parentSchematics.Contains(block.ParentId)) // The block is a child of some schematic inside "parentSchematics" array, therefore it will be skipped to avoid spawning it and its children twice.
                    continue;

                CreateRecursiveFromID(block.ObjectId, blocks, childGameObjectTransform); // The child now becomes the parent
            }
        }

        private Transform CreateObject(SchematicBlockData block, Transform parentTransform)
        {
            if (block == null)
                return null;

            GameObject gameObject = null;
            RuntimeAnimatorController animatorController;
            SerializableRigidbody serializableRigidbody;

            switch (block.BlockType)
            {
                case BlockType.Empty:
                    {
                        gameObject = new GameObject(block.Name)
                        {
                            layer = 2, // Ignore Raycast
                        };

                        gameObject.transform.parent = parentTransform;
                        gameObject.transform.localPosition = block.Position;
                        gameObject.transform.localEulerAngles = block.Rotation;

                        /*
                        if (_serializableRigidbodies is not null && _serializableRigidbodies.TryGetValue(block.ObjectId, out serializableRigidbody))
                        {
                            Rigidbody rigidbody = gameObject.AddComponent<Rigidbody>();
                            rigidbody.isKinematic = serializableRigidbody.IsKinematic;
                            rigidbody.useGravity = serializableRigidbody.UseGravity;
                            rigidbody.constraints = serializableRigidbody.Constraints;
                            rigidbody.mass = serializableRigidbody.Mass;
                        }
                        */

                        AttachedBlocks.Add(gameObject);
                        ObjectFromId.Add(block.ObjectId, gameObject.transform);

                        break;
                    }

                case BlockType.Primitive:
                    {
                        if (Instantiate(ObjectType.Primitive.GetObjectByMode(), parentTransform).TryGetComponent(out PrimitiveObjectToy primitiveToy))
                        {
                            PrimitiveObject primitiveObject = primitiveToy.gameObject.AddComponent<PrimitiveObject>().Init(block);
                            gameObject = primitiveObject.gameObject;

                            if (Config.SchematicBlockSpawnDelay == -1f)
                            {
                                NetworkServer.Spawn(gameObject);
                            }
                            else
                            {
                                Timing.RunCoroutine(SpawnDelayed(gameObject));
                            }

                            /*
                            if (_serializableRigidbodies is not null && _serializableRigidbodies.TryGetValue(block.ObjectId, out serializableRigidbody))
                            {
                                primitiveObject.Rigidbody = primitiveObject.gameObject.AddComponent<Rigidbody>();
                                primitiveObject.Rigidbody.isKinematic = serializableRigidbody.IsKinematic;
                                primitiveObject.Rigidbody.useGravity = serializableRigidbody.UseGravity;
                                primitiveObject.Rigidbody.constraints = serializableRigidbody.Constraints;
                                primitiveObject.Rigidbody.mass = serializableRigidbody.Mass;
                            }
                            */

                            AttachedBlocks.Add(primitiveToy.gameObject);
                            ObjectFromId.Add(block.ObjectId, gameObject.transform);
                        }

                        break;
                    }

                case BlockType.Light:
                    {
                        if (Instantiate(ObjectType.LightSource.GetObjectByMode(), parentTransform).TryGetComponent(out LightSourceToy lightSourceToy))
                        {
                            gameObject = lightSourceToy.gameObject.AddComponent<LightSourceObject>().Init(block).gameObject;

                            if (Config.SchematicBlockSpawnDelay == -1f)
                            {
                                NetworkServer.Spawn(gameObject);
                            }
                            else
                            {
                                Timing.RunCoroutine(SpawnDelayed(gameObject));
                            }

                            if (TryGetAnimatorController(block.AnimatorName, out animatorController))
                                Timing.RunCoroutine(AddAnimatorDelayed(lightSourceToy._light.gameObject, animatorController));

                            AttachedBlocks.Add(gameObject);
                            ObjectFromId.Add(block.ObjectId, gameObject.transform);
                        }

                        return gameObject.transform;
                    }

                case BlockType.Pickup:
                    {
                        Pickup pickup = Item.Create((ItemType)Enum.Parse(typeof(ItemType), block.Properties["ItemType"].ToString())).CreatePickup(Vector3.zero);
                        gameObject = pickup.Base.gameObject;
                        gameObject.name = block.Name;

                        gameObject.transform.parent = parentTransform;
                        gameObject.transform.localPosition = block.Position;
                        gameObject.transform.localEulerAngles = block.Rotation;
                        gameObject.transform.localScale = block.Scale;

                        if (block.Properties.ContainsKey("Kinematic"))
                            pickup.Base.Rb.isKinematic = true;

                        if (block.Properties.ContainsKey("Locked"))
                            ItemSpawnPointObject.LockedPickups.Add(pickup);

                        if (Config.SchematicBlockSpawnDelay == -1f)
                            NetworkServer.Spawn(gameObject);
                        else
                            Timing.RunCoroutine(SpawnDelayed(gameObject));

                        AttachedBlocks.Add(gameObject);
                        ObjectFromId.Add(block.ObjectId, gameObject.transform);

                        return gameObject.transform;
                    }

                case BlockType.Workstation:
                    {
                        if (Instantiate(ObjectType.WorkStation.GetObjectByMode(), parentTransform).TryGetComponent(out InventorySystem.Items.Firearms.Attachments.WorkstationController workstation))
                        {
                            gameObject = workstation.gameObject.AddComponent<WorkstationObject>().Init(block).gameObject;

                            gameObject.transform.parent = null;
                            NetworkServer.Spawn(gameObject);

                            AttachedBlocks.Add(gameObject);
                            _workstationsTransformProperties.Add(gameObject.transform.GetInstanceID(), block.ObjectId);
                            ObjectFromId.Add(block.ObjectId, gameObject.transform);
                        }

                        return gameObject.transform;
                    }

                case BlockType.Schematic:
                    {
                        string schematicName = block.Properties["SchematicName"].ToString();

                        gameObject = ObjectSpawner.SpawnSchematic(schematicName, transform.position + block.Position, Quaternion.Euler(transform.eulerAngles + block.Rotation)).gameObject;
                        gameObject.transform.parent = parentTransform;

                        gameObject.name = schematicName;

                        AttachedBlocks.Add(gameObject);
                        ObjectFromId.Add(block.ObjectId, gameObject.transform);

                        return gameObject.transform;
                    }
            }

            if (TryGetAnimatorController(block.AnimatorName, out animatorController))
                Timing.RunCoroutine(AddAnimatorDelayed(gameObject, animatorController));

            return gameObject.transform;
        }

        private bool TryGetAnimatorController(string animatorName, out RuntimeAnimatorController animatorController)
        {
            animatorController = null;

            if (!string.IsNullOrEmpty(animatorName))
            {
                Object animatorObject = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(x => x.mainAsset.name == animatorName)?.LoadAllAssets().First(x => x is RuntimeAnimatorController);

                if (animatorObject == null)
                {
                    string path = Path.Combine(DirectoryPath, animatorName);

                    if (!File.Exists(path))
                    {
                        Log.Warn($"{gameObject.name} block of {name} should have a {animatorName} animator attached, but the file does not exist!");
                        return false;
                    }

                    animatorObject = AssetBundle.LoadFromFile(path).LoadAllAssets().First(x => x is RuntimeAnimatorController);
                }

                animatorController = animatorObject as RuntimeAnimatorController;
                return true;
            }

            return false;
        }

        private IEnumerator<float> AddAnimatorDelayed(GameObject gameObject, RuntimeAnimatorController animatorController)
        {
            Animator animator = gameObject.AddComponent<Animator>();
            yield return Timing.WaitUntilTrue(() => IsBuilt);
            animator.runtimeAnimatorController = animatorController;
        }

        private IEnumerator<float> SpawnDelayed(GameObject gameObject)
        {
            yield return Timing.WaitForSeconds(Config.SchematicBlockSpawnDelay * AttachedBlocks.Count);

            NetworkServer.Spawn(gameObject);

            if (Base.CullingType != CullingType.Distance)
                yield break;

            if (gameObject.TryGetComponent(out NetworkIdentity networkIdentity))
            {
                Timing.CallDelayed(0.1f, () =>
                {
                    foreach (Player player in Player.List)
                    {
                        player.DestroyNetworkIdentity(networkIdentity);
                    }
                });
            }
        }

        private void CreateTeleporters()
        {
            string teleportPath = Path.Combine(DirectoryPath, $"{Name}-Teleports.json");
            if (!File.Exists(teleportPath))
                return;

            foreach (SerializableTeleport teleport in JsonSerializer.Deserialize<List<SerializableTeleport>>(File.ReadAllText(teleportPath)))
            {
                GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                gameObject.name = teleport.Name;
                gameObject.transform.localScale = teleport.Scale;

                if (teleport.RoomType == RoomType.Surface)
                {
                    gameObject.transform.parent = ObjectFromId[teleport.ParentId];
                    gameObject.transform.localPosition = teleport.Position;
                    gameObject.transform.localEulerAngles = teleport.Rotation;
                }
                else
                {
                    Room room = API.GetRandomRoom(teleport.RoomType);
                    gameObject.transform.position = API.GetRelativePosition(teleport.Position, room);
                    gameObject.transform.rotation = API.GetRelativeRotation(teleport.Rotation, room);
                    gameObject.transform.parent = ObjectFromId[teleport.ParentId];
                }

                ObjectFromId.Add(teleport.ObjectId, gameObject.transform);

                gameObject.AddComponent<TeleportObject>().Init(teleport, this);
            }
        }

        private void AddRigidbodies()
        {
            string rigidbodyPath = Path.Combine(DirectoryPath, $"{Name}-Rigidbodies.json");
            if (!File.Exists(rigidbodyPath))
                return;

            foreach (KeyValuePair<int, SerializableRigidbody> dict in JsonSerializer.Deserialize<Dictionary<int, SerializableRigidbody>>(File.ReadAllText(rigidbodyPath)))
            {
                Rigidbody rigidbody = ObjectFromId[dict.Key].gameObject.AddComponent<Rigidbody>();
                rigidbody.isKinematic = dict.Value.IsKinematic;
                rigidbody.useGravity = dict.Value.UseGravity;
                rigidbody.constraints = dict.Value.Constraints;
                rigidbody.mass = dict.Value.Mass;
            }
        }

        private void OnDestroy()
        {
            Patches.OverridePositionPatch.ResetValues();
            AnimationController.Dictionary.Remove(this);

            // TEMP
            foreach (GameObject gameObject in AttachedBlocks)
            {
                if (_workstationsTransformProperties.ContainsKey(gameObject.transform.GetInstanceID()))
                    NetworkServer.Destroy(gameObject);
            }

            Events.Handlers.Schematic.OnSchematicDestroyed(new Events.EventArgs.SchematicDestroyedEventArgs(this, Name));
        }

        internal bool IsBuilt = false;
        internal Dictionary<int, Transform> ObjectFromId = new();

        private ReadOnlyCollection<NetworkIdentity> _networkIdentities;
        private Dictionary<int, int> _workstationsTransformProperties = new();

        private static readonly Config Config = MapEditorReborn.Singleton.Config;
    }
}
