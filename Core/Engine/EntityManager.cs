using Chisel.Collision;
using Chisel.EXScript;
using Chisel.Utils;
using Engine.Compilation;
using Engine.Conditions;
using Engine.Console;
using Engine.Physics;
using Engine.Rendering;
using Engine.Scripting.IO;
using Engine.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Rockwall;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Engine
{
    public static class EntityManager
    {
        public const int MaxEntities = 65536;
        public static FixedList<WorldEntity> entities = new FixedList<WorldEntity>(MaxEntities);

        public static int CurrentFrame = 0;

        static Queue<WorldEntity> despawn = new Queue<WorldEntity>();
        static Queue<WorldEntity> spawn = new Queue<WorldEntity>();
        static Dictionary<string, List<int>> entityNamesToIndices = new Dictionary<string, List<int>>();

        public static bool NewLoad = false;

        public static void UpdateEntities(GameTime gameTime)
        {
            while (spawn.Count > 0)
            {
                var entity = spawn.Dequeue();
                int slot = entities.Add(entity);

                if (!string.IsNullOrEmpty(entity.Name))
                {
                    if (entityNamesToIndices.TryGetValue(entity.Name, out List<int> value)) value.Add(slot);
                    else entityNamesToIndices.Add(entity.Name, new List<int>() { slot });
                }
                Blockmap.UpdateEntity(entity);
            }

            while (despawn.Count > 0)
            {
                WorldEntity entity = despawn.Dequeue();

                if (entity == null) continue;

                int index = entities.FindIndex(e => e == entity);

                if (!string.IsNullOrEmpty(entity.Name) && entityNamesToIndices.TryGetValue(entity.Name, out List<int> value))
                {
                    value.Remove(index);
                    if (value.Count <= 0) entityNamesToIndices.Remove(entity.Name);
                }

                entity.Bounds = new BoundingBox();
                entity.IsSimulated = false;
                entity.IgnoreCollision = true;

                entities.Remove(entity);

                Blockmap.RemoveEntity(entity);
            }

            if (NewLoad)
            {
                OnAllEntitiesSpawned();
                NewLoad = false;
            }

            var vals = entities.GetValues();

            foreach (var entity in vals)
            {
                if (entity.IsLight) continue;

                entity?.PreUpdate();

                var leafBits = entity.GetLeafBits();

                entity.willBeRenderedThisFrame = RenderEngine.IsAnyLeafInMainPVS(leafBits);
                entity.willBeRenderedInSkyboxThisFrame = RenderEngine.IsAnyLeafInSkyPVS(leafBits);
            }
            foreach (var entity in vals)
            {
                if (entity.IsLight) continue;

                entity?.Update(gameTime);
                if (entity != null) Blockmap.UpdateEntity(entity);
            }
            CurrentFrame++;
            foreach (var entity in vals)
            {
                if (entity.IsLight) continue;

                entity?.RefreshWorldTransform(CurrentFrame);

                entity?.UpdatePhysicsBody();
            }
        }
        public static void ResolveMoveParentsAndConvertToLocal(List<WorldEntity> batch)
        {
            var absoluteCache = new Dictionary<WorldEntity, Matrix>();
            CurrentFrame++;

            void ResolveOne(WorldEntity entity)
            {
                if (absoluteCache.ContainsKey(entity)) return;

                Matrix absolute = entity.LocalMatrix;

                if (entity.MoveParent != null)
                {
                    ResolveOne(entity.MoveParent);
                    Matrix parentAbsolute = absoluteCache[entity.MoveParent];

                    Matrix local = absolute * Matrix.Invert(parentAbsolute);
                    local.Decompose(out Vector3 lscale, out Quaternion lrot, out Vector3 lpos);

                    entity.Position = lpos;
                    entity.Rotation = lrot;
                    entity.Scale = lscale;
                }

                absolute.Decompose(out Vector3 wscale, out Quaternion wrot, out Vector3 wpos);
                entity.SetWorldTransformCache(wpos, wrot, wscale);
                entity.MarkWorldTransformFresh(CurrentFrame);

                absoluteCache[entity] = absolute;
            }

            foreach (var entity in batch)
            {
                ResolveOne(entity);
            }
        }
        public static void OnAllEntitiesSpawned()
        {
            foreach (var entity in entities.GetValues())
            {
                if (entity.IsLight) continue;

                entity?.Controller?.OnAllEntitiesSpawned();
            }
            EXValuePersistence.ResolveAllPending();
            RenderEngine.RegisterPlanarReflectors();
            RenderEngine.GenRuntimeCubemapAssociation();
        }
        public static void PostUpdateEntities(GameTime gameTime)
        {
            foreach (var entity in entities.GetValues())
            {
                if (entity.IsLight) continue;

                entity?.UpdateOnGround();
                entity?.MergePhysicsResults();
                entity?.HandleQueuedTasks();
            }
        }
        public static void RenderEntities(GameTime gameTime, bool deferredPass, bool flipWinding = false, bool useSkyboxVisibility = false)
        {
            foreach (var entity in entities.GetValues())
            {
                if (entity == null) continue;
                if (entity.IsLight) continue;
                bool visible = useSkyboxVisibility ? entity.willBeRenderedInSkyboxThisFrame : entity.willBeRenderedThisFrame;
                if (!visible) continue;
                if (entity.DeferRenderTillLast != deferredPass) continue;

                if (entity is BrushEntity b)
                {
                    MainEngine.Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;
                    foreach (var brushIdx in b.brushSet)
                        RenderEngine.DrawBrush(brushIdx, false);
                    MainEngine.Instance.GraphicsDevice.RasterizerState = flipWinding ? RasterizerState.CullClockwise : RasterizerState.CullCounterClockwise;
                }

                entity.Render(gameTime);
            }
        }
        public static void BeforeRenderEntities(GameTime gameTime)
        {
            foreach (var entity in entities.GetValues())
            {
                if (entity == null) continue;
                if (entity.IsLight) continue;
                if (!entity.willBeRenderedThisFrame) continue;

                entity.BeforeRender(gameTime);
            }
        }

        public static void SpawnEntity(WorldEntity worldEntity, CustomSaveData? data = null, Guid? existingSaveID = null)
        {
            worldEntity.SaveID = existingSaveID ?? Guid.NewGuid();

            CurrentFrame++;
            worldEntity.RefreshWorldTransform(CurrentFrame);

            worldEntity.Spawn();
            worldEntity.RestoreCustomData(data);

            spawn.Enqueue(worldEntity);
        }
        public static void DespawnEntity(WorldEntity worldEntity)
        {
            despawn.Enqueue(worldEntity);
            worldEntity.OnDespawned();
        }
        public static void DespawnAllEntities()
        {
            if (entities.Count == 0) return;
            foreach (var entity in entities.GetValues())
            {
                DespawnEntity(entity);
            }
        }
        public static int[] FindEntityIndexByName(string name)
        {
            if (string.IsNullOrEmpty(name) || !entityNamesToIndices.ContainsKey(name)) return null;

            return entityNamesToIndices[name].ToArray();
        }
        public static WorldEntity FindSingleEntityByName(string name)
        {
            if (string.IsNullOrEmpty(name) || !entityNamesToIndices.ContainsKey(name)) return null;
            var entid = entityNamesToIndices[name].ToArray();
            if (entid == null || entid.Length == 0 || entid[0] == -1) return null;

            return entities[entid[0]];
        }
    }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class EntityDescriptor : Attribute
    {
    }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class ExposeEntityProperty : Attribute
    {
        public string name;
        public string hint;
        public EntityPropertyType type;
        public string category;
        public string defaultValue;
        public float min, max;

        public ExposeEntityProperty(string name, EntityPropertyType type, string hint = "", string category = "General", string defaultValue = "", float min = 0, float max = 0)
        {
            this.name = name;
            this.hint = hint;
            this.type = type;
            this.category = category;
            this.defaultValue = defaultValue;
            this.min = min;
            this.max = max;
        }
    }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class ExposeEntityPropertyEnum : Attribute
    {
        public string name;
        public string hint;
        public string category;
        public string[] options;

        public ExposeEntityPropertyEnum(string name, string hint, string category, params string[] options)
        {
            this.name = name;
            this.hint = hint;
            this.category = category;
            this.options = options;
        }
    }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class ExposeEntityPropertyTarget : Attribute
    {
        public string name;
        public string hint;
        public string category;
        public string classFilter;

        public ExposeEntityPropertyTarget(string name, string hint = "", string category = "General", string classFilter = null)
        {
            this.name = name;
            this.hint = hint;
            this.category = category;
            this.classFilter = classFilter;
        }
    }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public class ExposeValue : Attribute
    {
        public string Key { get; }
        public ExposeValue(string key) => Key = key;
    }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public class SaveValue : Attribute
    {
        public string Key { get; }
        public SaveValue(string key) => Key = key;
    }
    public class BrushEntity : WorldEntity
    {
        /// <summary>
        /// Indices into GlobalMapData.activeMap.brushes that this entity owns and moves/animates as
        /// one rigid group.
        /// </summary>
        public int[] brushSet = Array.Empty<int>();
        /// <summary>Convenience accessor for the common single-brush case (and as an anchor/pivot brush for multi-brush entities).</summary>
        public int PrimaryBrush => brushSet.Length > 0 ? brushSet[0] : -1;
        public Vector3 SpawnAnchor;

        [JsonIgnore] public List<WorldEntity> insideBrush = new List<WorldEntity>();
        [JsonIgnore] public bool ignoreWorldCollision;
        private bool initialSweep = false;
        private int sweepFrameCount = 4;

        /// <summary>World-space union of every owned brush's at-rest (compile-time) bounds.</summary>
        public BoundingBox GetBrushSetWorldBounds()
        {
            var brushBounds = GlobalMapData.ActiveMap.BrushBounds;
            if (brushSet.Length == 0 || brushBounds == null) return new BoundingBox();

            BoundingBox result = brushBounds[brushSet[0]];
            for (int i = 1; i < brushSet.Length; i++)
                result = BoundingBox.CreateMerged(result, brushBounds[brushSet[i]]);
            return result;
        }

        public override void Spawn()
        {
            SpawnAnchor = WorldPosition;

            var worldBounds = GetBrushSetWorldBounds();
            Bounds = new BoundingBox(worldBounds.Min - WorldPosition, worldBounds.Max - WorldPosition);

            base.Spawn();
        }

        protected override JoltPhysicsSharp.Shape BuildCollisionShape(Vector3 half, Vector3 center)
        {
            return PhysicsEngine.BuildBrushSetShape(brushSet, SpawnAnchor + center, PhysicsDensityMultiplier)
                ?? base.BuildCollisionShape(half, center);
        }
        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (!initialSweep)
            {
                sweepFrameCount--;
                if (sweepFrameCount > 0) return;

                initialSweep = true;
                var sweepBounds = GetBrushSetWorldBounds();
                foreach (var worldEntity in EntityManager.entities.GetValues())
                {
                    if (worldEntity == null || worldEntity == this) continue;
                    if (worldEntity is BrushEntity) continue;
                    if (insideBrush.Contains(worldEntity)) continue;

                    if (sweepBounds.Intersects(worldEntity.Bounds))
                    {
                        insideBrush.Add(worldEntity);
                        OnEntityEnter(worldEntity);
                    }
                }
            }
        }
        public virtual void OnEntityEnter(WorldEntity entity) { }
        public virtual void OnEntityExit(WorldEntity entity) { }
    }
    [RegisterEntityOutputs("DamageTaken", "Destroyed")]
    [RegisterEntityInputs("AddVelocity", "SetVelocity", "SetPosition", "SetScale", "SetRotation", "AddImpulse", "Destroy", "GetValue", "TakeDamage")]
    public class WorldEntity
    {
        public EntityProperty[] properties;
        public bool IsDespawned { get; private set; } = false;
        public bool DeferRenderTillLast;
        public Guid SaveID;

        public delegate string[] InputHandler(string param, WorldEntity from, string[] passVariables);
        private enum OutputStepKind { Wait, Cast }
        private readonly struct OutputStep
        {
            public readonly OutputStepKind Kind;
            public readonly float Seconds;
            OutputStep(OutputStepKind kind, float seconds) { Kind = kind; Seconds = seconds; }
            public static OutputStep Wait(float s) => new(OutputStepKind.Wait, s);
            public static OutputStep Cast() => new(OutputStepKind.Cast, 0);
        }

        public Dictionary<string, EntityOutput[]> EntityOutputs;
        public Dictionary<string, InputHandler> EntityInputs;
        private List<EXScriptHandle> liveIOScripts = new List<EXScriptHandle>();

        internal List<(float delay, Action onComplete)> queuedTasks = new();

        internal bool willBeRenderedThisFrame;
        internal bool willBeRenderedInSkyboxThisFrame;
        public bool WillRenderInSkyboxThisFrame => willBeRenderedInSkyboxThisFrame;
        public bool WillRenderThisFrame => willBeRenderedThisFrame;


        private BoundingBox bounds;
        private BoundingBox renderBounds;
        /// <summary>
        /// Automatically calculated OBB from the entities bounds.
        /// </summary>
        public OrientedBoundingBox OrientedBounds;
        private OrientedBoundingBox orientedRenderBounds;

        [JsonIgnore] public Vector3 Position;
        [JsonIgnore] public Quaternion Rotation = Quaternion.Identity;
        /// <summary>
        /// Describes rotation, in euler angles, that this object was spawned with from the map.
        /// </summary>
        [JsonIgnore] public Vector3 SpawnRotation;
        [JsonIgnore] public Vector3 Scale, Velocity;

        [JsonIgnore] public WorldEntity MoveParent;

        [JsonIgnore] private Vector3 cachedWorldPosition;
        [JsonIgnore] private Quaternion cachedWorldRotation = Quaternion.Identity;
        [JsonIgnore] private Vector3 cachedWorldScale = Vector3.One;

        [JsonIgnore] public Vector3 WorldPosition => MoveParent == null ? Position : cachedWorldPosition;
        [JsonIgnore] public Quaternion WorldRotation => MoveParent == null ? Rotation : cachedWorldRotation;
        [JsonIgnore] public Vector3 WorldScale => MoveParent == null ? Scale : cachedWorldScale;

        private int worldTransformFrame = -1;

        public Matrix LocalMatrix => Matrix.CreateScale(Scale) * Matrix.CreateFromQuaternion(Rotation) * Matrix.CreateTranslation(Position);
        public Matrix WorldTransformMatrix => Matrix.CreateScale(WorldScale) * Matrix.CreateFromQuaternion(WorldRotation) * Matrix.CreateTranslation(WorldPosition);
        /// <summary>
        /// What collision 'layer' this object resides on.
        /// </summary>
        [JsonIgnore]
        public sbyte CollisionID
        {
            get
            {
                return collisionID;
            }
            set
            {
                collisionID = value;
            }
        }
        [JsonIgnore] private sbyte collisionID = 0;
        /// <summary>
        /// Properties of the brush face this entity is currently on top of, or the last valid face if none is available.
        /// </summary>
        [JsonIgnore] public Rockwall.Material StandingOnSurface;

        [JsonIgnore] private Vector3 previousPosition;
        [JsonIgnore] private Vector3 previousVelocity;
        [JsonIgnore] private Vector3 previousScale;
        public Vector3 GetPreviousPosition() => previousPosition;
        public Vector3 GetPreviousVelocity() => previousVelocity;

        [JsonIgnore] public bool IsOnGround, IsSimulated, IsLight, AxisAlignedBox;
        [JsonIgnore] public bool IsKinematic;
        [JsonIgnore]
        public bool IgnoreCollision
        {
            get { return ignoreCollision; }
            set
            {
                ignoreCollision = value;
            }
        }
        [JsonIgnore]
        public bool IgnoreGravity
        {
            get { return ignoreGravity; }
            set
            {
                ignoreGravity = value;
            }
        }
        [JsonIgnore] public bool IgnoreRagdolls;
        [JsonIgnore] public bool WasOnGround;
        [JsonIgnore] public bool WasJustReleasedFromConstraint;
        [JsonIgnore] public bool IsSubmerged;
        [JsonIgnore] public bool AllowGroundSeparation;
        [JsonIgnore] private bool justStepped;
        [JsonIgnore] private bool ignoreCollision;
        [JsonIgnore] private bool ignoreGravity = false;

        /// <summary>
        /// Describes the BoundingBox of the object, in world space.
        /// </summary>
        public BoundingBox Bounds
        {
            get
            {
                return new BoundingBox(bounds.Min * WorldScale + WorldPosition, bounds.Max * WorldScale + WorldPosition);
            }
            set
            {
                bounds = value;

                OrientedBounds = new OrientedBoundingBox(bounds);

                if (!PhysicsEngine.AllBodies.Contains(PhysicsBodyID))
                {
                    return;
                }
                else
                {
                    UpdateBounds();
                }
            }
        }
        public BoundingBox RenderBounds
        {
            get
            {
                return orientedRenderBounds.GetBoundingBox();
            }
            set
            {
                renderBounds = value;

                orientedRenderBounds = new OrientedBoundingBox(renderBounds);
            }
        }
        /// <returns>Raw BoundingBox of the entity, not affected by entity transform.</returns>
        public BoundingBox GetRealBounds() => bounds;
        /// <summary>
        /// The name of the entity according to the map, used for targeting.
        /// </summary>

        [JsonIgnore] public string Name;
        /// <summary>
        /// The controller of the entity, this is the 'brain' of the entity.
        /// </summary>
        public EntityController Controller
        {
            get; set;
        }
        /// <summary>
        /// The physics type of the object, used for impact sounds and the like.
        /// </summary>
        [JsonIgnore] public string PhysicsType;

        public enum EntityPhysicsShapes
        {
            Box,
            Cylinder
        }
        [JsonIgnore] public EntityPhysicsShapes PhysicsShape;
        [JsonIgnore] public float PhysicsRestitution;
        [JsonIgnore] public float PhysicsDensityMultiplier = 1f;
        [JsonIgnore] public List<WorldEntity> IgnoredEntities = new List<WorldEntity>();

        private List<Vector3> impulsesToAdd = new List<Vector3>();
        private List<(Vector3 f, Vector3 p)> impulsesToAddFromPosition = new List<(Vector3 f, Vector3 p)>();
        private List<Vector3> forcesToAdd = new List<Vector3>();
        private List<(Vector3 f, Vector3 p)> forcesToAddFromPosition = new List<(Vector3 f, Vector3 p)>();

        private List<ICondition> conditions = new List<ICondition>();
        private List<int> conditionIDs = new List<int>();

        public JoltPhysicsSharp.BodyID PhysicsBodyID;
        private JoltPhysicsSharp.Body physicsBodyCache;
        private bool physicsBodyCached;
        private bool physicsBodyCreated;
        public JoltPhysicsSharp.Body PhysicsBody
        {
            get
            {
                if (physicsBodyCached) return physicsBodyCache;

                physicsBodyCached = true;
                var body = PhysicsEngine.GetFromBodyID(PhysicsBodyID);
                physicsBodyCache = body;

                return body;
            }
        }

        Vector3 bodyOffset;
        public Vector3 BodyOffset => PhysicsBody == null ? Vector3.Zero : Vector3.Transform(bodyOffset, Matrix.CreateFromQuaternion(PhysicsBody.Rotation));

        private const float KneeHeight = 0.5f;
        private const float SkinWidth = 0.08f;
        const float SnapTolerance = 0.04f;

        private Vector3 lastGroundNormal = Vector3.Up;

        [JsonIgnore] private ulong[] leafBits;
        [JsonIgnore] private Vector3 leafBitsPosition;
        [JsonIgnore] private bool leafBitsValid;

        public ulong[] GetLeafBits()
        {
            if (leafBitsValid && Position == leafBitsPosition && leafBits != null)
                return leafBits;

            leafBits ??= Bitset.Create(BSPRoot.Nodes.Length);
            Array.Clear(leafBits);
            Collision.GatherLeaves(0, Bounds, leafBits);

            leafBitsPosition = Position;
            leafBitsValid = true;

            return leafBits;
        }

        public WorldEntity()
        {
        }
        protected virtual JoltPhysicsSharp.Shape BuildCollisionShape(Vector3 half, Vector3 center)
        {
            switch (PhysicsShape)
            {
                default:
                    var b = new JoltPhysicsSharp.BoxShape(new System.Numerics.Vector3(half.X, half.Y, half.Z), 0.01f);
                    b.Density *= PhysicsDensityMultiplier;
                    return b;
                case EntityPhysicsShapes.Cylinder:
                    var c = new JoltPhysicsSharp.CylinderShape(half.Y, float.Max(half.X, half.Z));
                    c.Density *= PhysicsDensityMultiplier;
                    return c;
            }
        }
        void UpdateBounds()
        {
            var min = bounds.Min * WorldScale;
            var max = bounds.Max * WorldScale;

            if (renderBounds.Max == renderBounds.Min) RenderBounds = bounds;

            var center = min + (max - min) / 2;

            bodyOffset = center;

            Vector3 dimensions = max - min;
            Vector3 half = dimensions * 0.5f;

            PhysicsEngine.ShapeMapper[PhysicsBodyID] = BuildCollisionShape(half, center);

            PhysicsEngine.BodyInterface.SetShape(PhysicsBodyID, PhysicsEngine.ShapeMapper[PhysicsBodyID], false, JoltPhysicsSharp.Activation.Activate);
        }
        void CreateBounds()
        {
            var min = bounds.Min * WorldScale;
            var max = bounds.Max * WorldScale;

            Vector3 dimensions = max - min;
            Vector3 half = dimensions * 0.5f;

            var center = min + (max - min) / 2;

            bodyOffset = center;

            var initialRotation = AxisAlignedBox ? Quaternion.Identity : WorldRotation;
            var worldOffset = Vector3.Transform(bodyOffset, Matrix.CreateFromQuaternion(initialRotation));

            JoltPhysicsSharp.Shape shape = BuildCollisionShape(half, center);
            var motion = (IsSimulated && !IsKinematic) ? JoltPhysicsSharp.MotionType.Dynamic : JoltPhysicsSharp.MotionType.Kinematic;

            using var bodySettings = new JoltPhysicsSharp.BodyCreationSettings(
                shape,
                (WorldPosition + worldOffset).ToNumerics(),
                initialRotation.ToNumerics(),
                motion,
                PhysicsEngine.Layers.Moving);

            bodySettings.Restitution = PhysicsRestitution;
            bodySettings.Friction = 2f;
            bodySettings.MotionQuality = JoltPhysicsSharp.MotionQuality.LinearCast;

            if (AxisAlignedBox)
            {
                bodySettings.InertiaMultiplier = 0;
                bodySettings.AllowedDOFs = JoltPhysicsSharp.AllowedDOFs.TranslationX |
                                           JoltPhysicsSharp.AllowedDOFs.TranslationY |
                                           JoltPhysicsSharp.AllowedDOFs.TranslationZ;
            }

            PhysicsBodyID = PhysicsEngine.BodyInterface.CreateAndAddBody(bodySettings, JoltPhysicsSharp.Activation.Activate);
            PhysicsEngine.AllBodies.Add(PhysicsBodyID);
            PhysicsEngine.AllShapes.Add(shape);

            PhysicsEngine.BodyMapper.Add(PhysicsBodyID, this);
            PhysicsEngine.ShapeMapper.Add(PhysicsBodyID, shape);

            if (renderBounds.Max == renderBounds.Min) RenderBounds = bounds;

            bodySettings.Dispose();
        }
        internal void SetWorldTransformCache(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            cachedWorldPosition = position;
            cachedWorldRotation = rotation;
            cachedWorldScale = scale;
        }
        internal void MarkWorldTransformFresh(int frame)
        {
            worldTransformFrame = frame;
        }
        public void RefreshWorldTransform(int frame)
        {
            if (worldTransformFrame == frame) return;
            worldTransformFrame = frame;

            if (MoveParent == null) return;

            MoveParent.RefreshWorldTransform(frame);

            Matrix world = LocalMatrix * MoveParent.WorldTransformMatrix;
            world.Decompose(out Vector3 scale, out Quaternion rotation, out Vector3 position);

            SetWorldTransformCache(position, rotation, scale);
        }
        public virtual void Spawn()
        {
            if (Controller == null) return;

            if (!physicsBodyCreated)
            {
                CreateBounds();
                physicsBodyCreated = true;
            }

            // I sure do love uncovering weird bugs caused by my jank ass physics engine from 3 years ago
            var initialRotation = AxisAlignedBox ? Quaternion.Identity : WorldRotation;
            var worldOffset = Vector3.Transform(bodyOffset, Matrix.CreateFromQuaternion(initialRotation));

            PhysicsEngine.BodyInterface.SetPositionRotationAndVelocity(
                PhysicsBodyID, (WorldPosition + worldOffset).ToNumerics(), initialRotation.ToNumerics(),
                Velocity.ToNumerics(), Vector3.Zero.ToNumerics());

            if (AxisAlignedBox)
            {
                PhysicsEngine.BodyInterface.SetRotation(PhysicsBodyID, System.Numerics.Quaternion.Identity, JoltPhysicsSharp.Activation.Activate);
            }

            //Default inputs:
            RegisterInputLocally("AddVelocity", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    Velocity += new Vector3(x, y, z);
                    PhysicsBody.SetLinearVelocity(Velocity.ToNumerics());
                }
            });
            RegisterInputLocally("SetVelocity", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    Velocity = new Vector3(x, y, z);
                    PhysicsBody.SetLinearVelocity(Velocity.ToNumerics());
                }
            });
            RegisterInputLocally("SetPosition", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    Position = new Vector3(x, y, z);
                }
            });
            RegisterInputLocally("SetScale", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    Scale = new Vector3(x, y, z);
                }
            });
            RegisterInputLocally("SetRotation", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    Rotation = CMath.ToQuaternion(new Vector3(MathHelper.ToRadians(x), MathHelper.ToRadians(y), MathHelper.ToRadians(z)));
                }
            });
            RegisterInputLocally("AddImpulse", (s, f) =>
            {
                string[] split = s.Trim().Split(',');

                if (split.Length >= 3 && float.TryParse(split[0], out float x) && float.TryParse(split[1], out float y) && float.TryParse(split[2], out float z))
                {
                    AddImpulse(new Vector3(x, y, z));
                }
            });
            RegisterInputLocally("Destroy", (s, f) => { EntityManager.DespawnEntity(this); });
            RegisterInputLocally("GetValue", (param, from, passVars) =>
            {
                if (string.IsNullOrEmpty(param)) return null;

                // Check controller first, then the entity itself
                var sources = new object[] { Controller, this };

                foreach (var source in sources)
                {
                    if (source == null) continue;

                    var accessors = ExposedValueCache.GetAccessors(source.GetType());

                    if (!accessors.TryGetValue(param, out var accessor)) continue;

                    var val = accessor.Getter(source);
                    if (val == null) return null;

                    // Inject as a variable named after the key that was requested
                    return new[] { $"{param}:{val}" };
                }

                Logger.AppendError($"GetValue: no exposed value '{param}' found on {GetType().Name}");
                return null;
            });
            RegisterInputLocally("SetValue", (param, from, passVars) =>
            {
                if (string.IsNullOrEmpty(param)) return null;

                if(!TrySplitKeyValue(param, out var key, out var value))
                {
                    Logger.AppendError($"SetValue: invalid format `{param}`, expected key:value");
                    return null;
                }

                var sources = new object[] { Controller, this };
                foreach (var source in sources)
                {
                    var accessors = ExposedValueCache.GetAccessors(source.GetType());
                    if (accessors.TryGetValue(key, out var accessor))
                    {
                        accessor.Setter(source, value);
                        return null;
                    }
                }

                Logger.AppendError($"SetValue: no exposed value '{param}' found on {GetType().Name}");
                return null;
            });
            RegisterInputLocally("TakeDamage", (s, f) =>
            {
                if (int.TryParse(s, out int val))
                {
                    TakeDamage(new DamageInfo { damage = val });
                }
            });

            Controller.entity = this;
            Controller.OnSpawn();
        }
        public void OnDespawned()
        {
            for(int i = conditions.Count-1; i >=0; i--)
            {
                var cond = conditions[i];

                RemoveCondition(cond);
            }

            IsDespawned = true;

            EntityInputs?.Clear();
            Controller?.OnDespawn();
        }
        public void PreUpdate()
        {
            if (WasJustReleasedFromConstraint)
            {
                //velocity = previousVelocity; Apparently this actually broke it
                WasJustReleasedFromConstraint = false;
            }
        }
        public virtual void Update(GameTime gameTime)
        {
            if (IsDespawned) return;
            justStepped = false;

            previousPosition = Position;
            previousVelocity = Velocity;

            for (int i = conditions.Count - 1; i >= 0; i--)
            {
                var cond = conditions[i];
                cond.Update(this);
            }

            Controller?.OnUpdate(gameTime);

            if (IsSimulated && !IsKinematic)
            {
                bool wantGravity = (!(IsOnGround && !AllowGroundSeparation) || !AxisAlignedBox) && !ignoreGravity;
                PhysicsEngine.BodyInterface.SetGravityFactor(PhysicsBodyID, wantGravity ? 1f : 0f);
            }

            if (Scale != previousScale)
            {
                UpdateBounds();
            }

            //UpdateTransforms();

            previousScale = Scale;
            WasOnGround = IsOnGround;

            orientedRenderBounds.Transformation = OrientedBounds.Transformation;
        }
        public void HandleQueuedTasks()
        {
            if (queuedTasks.Count > 0)
            {
                var item = queuedTasks.First();
                item.delay -= MainEngine.PreviousFrameDelta;
                if (item.delay < 0)
                {
                    item.onComplete();
                    queuedTasks.RemoveAt(0);
                    return;
                }
                queuedTasks[0] = item;
            }
        }
        public void MergePhysicsResults()
        {
            if ((IsSimulated || IsKinematic) && PhysicsBody != null)
                Position = PhysicsBody.Position - BodyOffset;

            // velocity/rotation only for true dynamic bodies
            if (IsSimulated && !IsKinematic && PhysicsBody != null)
            {
                Velocity = PhysicsBody.GetLinearVelocity();
                if (!AxisAlignedBox)
                    Rotation = PhysicsBody.Rotation;
            }
        }
        public void UpdateOnGround()
        {
            if (!PhysicsBodyID.IsValid) return;
            if (PhysicsBody == null) return;

            if (IsSubmerged) { IsOnGround = false; return; }

            IsOnGround = IsGrounded();
            if (IsOnGround && Velocity.Y < 0.1f) AllowGroundSeparation = false;
            if (justStepped) Velocity = previousVelocity;
        }
        public bool IsSeparatingFromGround()
        {
            float normalSpeed = Vector3.Dot(Velocity, lastGroundNormal);
            float predictedTravel = normalSpeed * MainEngine.PreviousFrameDelta;
            return predictedTravel > SnapTolerance;
        }
        bool IsGrounded(float maxSlopeAngleDegrees = 50f)
        {
            if (!PhysicsBodyID.IsValid) return false;
            if (justStepped) return true;

            float maxSlopeRadians = MathHelper.ToRadians(maxSlopeAngleDegrees);
            float minYComponent = (float)Math.Cos(maxSlopeRadians);

            var body = PhysicsBody;
            if (body == null || !PhysicsEngine.ContactInfo.TryGetValue(PhysicsBodyID, out var contacts)) return false;

            foreach (var kvp in contacts)
            {
                var contact = kvp.Value;

                if (PhysicsEngine.BodyInterface.GetObjectLayer(contact.other) == PhysicsEngine.Layers.Trigger) continue;

                if (Math.Abs(contact.normal.Y) > minYComponent && contact.point.Y < OrientedBounds.Center.Y)
                {
                    lastGroundNormal = contact.normal;

                    if (TryFindGroundSurface(contact.point, out var surfaceName))
                        StandingOnSurface = surfaceName;

                    return true;
                }
            }

            return false;
        }
        static bool TryFindGroundSurface(Vector3 contactPoint, out Material surface)
        {
            Material? brushSurface = null;
            float brushDist = float.MaxValue;

            var leaf = BSPRoot.Traverse(contactPoint);
            var brushID = BSPRoot.Nodes[leaf].brush;
            if (brushID > 0 && brushID < GlobalMapData.ActiveMap.Brushes.Length)
            {
                var brush = GlobalMapData.ActiveMap.Brushes[brushID];
                foreach (var face in brush.Faces)
                {
                    if (face.Plane.HasValue && face.Plane.Value.DotCoordinate(contactPoint) > 0)
                    {
                        brushSurface = GlobalMapData.LoadedMaterials[face.Surface];
                        brushDist = MathF.Abs(face.Plane.Value.DotCoordinate(contactPoint));
                        break;
                    }
                }
            }
            Material? terrainSurface = null;
            float terrainDist = float.MaxValue;

            if (GlobalMapData.ActiveMap.Terrains != null)
            {
                for (int i = 0; i < GlobalMapData.ActiveMap.Terrains.Length; i++)
                {
                    var terrain = GlobalMapData.ActiveMap.Terrains[i];

                    var expanded = terrain.Bounds;
                    expanded.Min -= new Vector3(0.5f);
                    expanded.Max += new Vector3(0.5f);
                    if (expanded.Contains(contactPoint) == ContainmentType.Disjoint) continue;

                    for (int t = 0; t < terrain.Triangles.Length; t += 3)
                    {
                        Vector3 A = terrain.Vertices[terrain.Triangles[t + 0]].Position;
                        Vector3 B = terrain.Vertices[terrain.Triangles[t + 1]].Position;
                        Vector3 C = terrain.Vertices[terrain.Triangles[t + 2]].Position;

                        var triMin = Vector3.Min(Vector3.Min(A, B), C);
                        var triMax = Vector3.Max(Vector3.Max(A, B), C);
                        if (contactPoint.X < triMin.X - 0.25f || contactPoint.X > triMax.X + 0.25f ||
                            contactPoint.Z < triMin.Z - 0.25f || contactPoint.Z > triMax.Z + 0.25f) continue;

                        Vector3 triNormal = Vector3.Normalize(Vector3.Cross(B - A, C - A));
                        var triPlane = new Plane(A, triNormal);
                        float dist = MathF.Abs(triPlane.DotCoordinate(contactPoint));

                        if (dist < terrainDist)
                        {
                            terrainDist = dist;
                            terrainSurface = GlobalMapData.LoadedMaterials[terrain.Surface];
                        }
                    }
                }
            }

            if (terrainSurface.HasValue && terrainDist < brushDist)
            {
                surface = terrainSurface.Value;
                return true;
            }
            if (brushSurface.HasValue)
            {
                surface = brushSurface.Value;
                return true;
            }

            surface = default;
            return false;
        }
        public void UpdatePhysicsBody()
        {
            if (!PhysicsBodyID.IsValid) return;
            if (PhysicsBody == null) return;
            var pos = WorldPosition + BodyOffset;

            if (AxisAlignedBox)
            {
                PhysicsEngine.BodyInterface.SetPositionAndRotationWhenChanged(PhysicsBodyID, pos.ToNumerics(), Quaternion.Identity.ToNumerics(), JoltPhysicsSharp.Activation.DontActivate);
            }
            else
            {
                PhysicsEngine.BodyInterface.SetPositionAndRotationWhenChanged(PhysicsBodyID, pos.ToNumerics(), WorldRotation.ToNumerics(), JoltPhysicsSharp.Activation.DontActivate);
            }

            PhysicsEngine.BodyInterface.SetLinearVelocity(PhysicsBodyID, Velocity.ToNumerics());

            if (!AxisAlignedBox) OrientedBounds.Transformation = Matrix.CreateScale(WorldScale) * Matrix.CreateTranslation((bounds.Max + bounds.Min) * 0.5f) * Matrix.CreateFromQuaternion(WorldRotation) * Matrix.CreateTranslation(WorldPosition);
            else OrientedBounds.Transformation = Matrix.CreateScale(WorldScale) * Matrix.CreateTranslation((bounds.Max + bounds.Min) * 0.5f) * Matrix.CreateTranslation(WorldPosition);

            if (impulsesToAdd.Count > 0 || impulsesToAddFromPosition.Count > 0)
            {
                foreach (var force in impulsesToAdd) PhysicsEngine.BodyInterface.AddImpulse(PhysicsBodyID, force.ToNumerics());
                foreach (var force in impulsesToAddFromPosition) PhysicsEngine.BodyInterface.AddImpulse(PhysicsBodyID, force.f.ToNumerics(), force.p.ToNumerics());

                impulsesToAdd.Clear();
                impulsesToAddFromPosition.Clear();
            }
            if (forcesToAdd.Count > 0 || forcesToAddFromPosition.Count > 0)
            {
                foreach (var force in forcesToAdd) PhysicsEngine.BodyInterface.AddForce(PhysicsBodyID, force.ToNumerics());
                foreach (var force in forcesToAddFromPosition) PhysicsEngine.BodyInterface.AddForce(PhysicsBodyID, force.f.ToNumerics(), force.p.ToNumerics());

                forcesToAdd.Clear();
                forcesToAddFromPosition.Clear();
            }
        }
        public void Render(GameTime gameTime)
        {
            if (Controller == null) return;

            Controller.OnRender(gameTime);
        }
        public void BeforeRender(GameTime gameTime)
        {
            if (Controller == null) return;

            Controller.OnBeforeRender(gameTime);
        }
        public void TakeDamage(DamageInfo info)
        {
            if (Controller == null) return;

            Controller.OnTakeDamage(info);
            CallOutput("DamageTaken", this, $"damage:{info.damage}");
        }

        public void AddForce(Vector3 f)
        {
            AllowGroundSeparation = true;
            forcesToAdd.Add(f);
        }
        public void AddForceAtPosition(Vector3 f, Vector3 p)
        {
            AllowGroundSeparation = true;
            forcesToAddFromPosition.Add((f, p));
        }

        public void AddImpulse(Vector3 f)
        {
            AllowGroundSeparation = true;
            impulsesToAdd.Add(f);
        }
        public void AddImpulseAtPosition(Vector3 f, Vector3 p)
        {
            AllowGroundSeparation = true;
            impulsesToAddFromPosition.Add((f, p));
        }

        public void AddCondition(ICondition condition)
        {
            if (conditionIDs.Contains(ConditionManager.GetID(condition.GetType()))) return;

            condition.OnAdded(this);
            conditions.Add(condition);
            conditionIDs.Add(ConditionManager.GetID(condition.GetType()));
        }
        public void RemoveCondition(ICondition condition)
        {
            condition.OnRemoved(this);
            conditions.Remove(condition);
            conditionIDs.Remove(ConditionManager.GetID(condition.GetType()));
        }
        public void RemoveCondition(Type conditionType)
        {
            var existing = conditions.FirstOrDefault(c => c.GetType() == conditionType);
            if (existing == null) return;
            RemoveCondition(existing);
        }

        public static bool TrySplitKeyValue(string param, out string key, out string value)
        {
            key = null;
            value = null;

            if (string.IsNullOrEmpty(param)) return false;

            int idx = param.IndexOf(':');
            if (idx <= 0) return false; // no separator found, or key would be empty

            key = param[..idx].Trim();
            value = param[(idx + 1)..].Trim();

            return key.Length > 0;
        }

        /// <summary>
        /// Attempts to move the entity by the given horizontal offset, stepping over small obstacles.
        /// </summary>
        public float TryStepUp(Vector3 wishDir)
        {
            if (!IsOnGround) return 0f;

            var horizontalDelta = new Vector3(wishDir.X + Velocity.X, 0, wishDir.Z + Velocity.Z) * PhysicsEngine.PhysicsFrameDelta;
            if (horizontalDelta.LengthSquared() < 0.0001f) return 0f;

            var start = Position;
            var target = start + horizontalDelta;

            if (IsPathClear(start, target, 0, 0.01f)) return 0f;

            var raisedStart = start + Vector3.Up * KneeHeight;
            var raisedTarget = raisedStart + horizontalDelta;

            if (!IsPathClear(raisedStart, raisedTarget, KneeHeight, 0.01f)) return 0f;

            BoundingBox bb = Bounds;
            Span<Vector3> downSamples = stackalloc Vector3[9];
            GetFootprintSamples(bb, 0.01f, KneeHeight, downSamples);

            float bestDrop = -1f;
            bool foundFloor = false;

            foreach (var point in downSamples)
            {
                var origin = point + horizontalDelta + Vector3.Up * SkinWidth;
                var ray = new Ray(origin, Vector3.Down);

                bool hit = Collision.CastPhysicsWorld(ray, KneeHeight + SkinWidth, out var result,
                    IgnoreRagdolls ? PhysicsFilters.IgnoreRagdollBroadPhaseRayCastFilter : null, PhysicsBodyID);

                if (!hit) continue;

                float drop = result.Fraction * (KneeHeight + SkinWidth);
                var normal = PhysicsEngine.GetFromBodyID(result.BodyID).GetWorldSpaceSurfaceNormal(result.subShapeID2, (ray.Position + ray.Direction * result.Fraction).ToNumerics());

                if (normal.Y < 0.9f) continue;
                
                if (!foundFloor || drop < bestDrop) { bestDrop = drop; foundFloor = true; }
            }

            if (!foundFloor) return 0f;

            float gained = (KneeHeight + SkinWidth) - bestDrop;
            gained = MathF.Min(gained, KneeHeight);
            if (gained <= 0f) return 0f;

            Position.Y += gained;
            Position.X += horizontalDelta.X;
            Position.Z += horizontalDelta.Z;
            PhysicsEngine.BodyInterface.SetPosition(PhysicsBodyID, Position.ToNumerics(), JoltPhysicsSharp.Activation.Activate);

            Velocity = previousVelocity;
            IsOnGround = true;
            justStepped = true;

            return gained;
        }
        public float TryStepDown()
        {
            if (!WasOnGround) return 0f;
            if (IsSeparatingFromGround()) return 0f;
            if (AllowGroundSeparation) return 0f;

            BoundingBox bb = Bounds;
            Span<Vector3> samples = stackalloc Vector3[9];
            GetFootprintSamples(bb, 0f, SkinWidth, samples);

            float bestDrop = -1f;
            bool foundFloor = false;

            foreach (var point in samples)
            {
                var ray = new Ray(point, Vector3.Down);
                bool hit = Collision.CastPhysicsWorld(ray, KneeHeight + SkinWidth, out var result,
                    IgnoreRagdolls ? PhysicsFilters.IgnoreRagdollBroadPhaseRayCastFilter : null, PhysicsBodyID);

                if (!hit) continue;

                float drop = result.Fraction * (KneeHeight + SkinWidth) - SkinWidth;
                if (!foundFloor || drop < bestDrop) { bestDrop = drop; foundFloor = true; }
            }

            if (!foundFloor || bestDrop <= 0f) return 0f;

            Position.Y -= bestDrop;
            if (Collision.CheckBounds(this, skipWorld: true, skipBrushEntities:true))
            {
                Position.Y += bestDrop;
                return 0f;
            }

            IsOnGround = true;
            return -bestDrop;
        }
        private bool IsPathClear(Vector3 from, Vector3 to, float heightOffset = 0, float inset = 0)
        {
            var delta = to - from;
            var distance = delta.Length();
            if (distance < float.Epsilon) return true;
            var dir = Vector3.Normalize(delta);

            BoundingBox bb = Bounds;
            Span<Vector3> samples = stackalloc Vector3[9];
            GetFootprintSamples(bb, inset, heightOffset, samples);

            foreach (var point in samples)
            {
                var origin = point + Vector3.Up * SkinWidth;
                bool hit = Collision.CastPhysicsWorld(new Ray(origin, dir), distance, out _,
                    IgnoreRagdolls ? PhysicsFilters.IgnoreRagdollBroadPhaseRayCastFilter : null, PhysicsBodyID);

                if (hit) return false;
            }

            return true;
        }
        private static void GetFootprintSamples(BoundingBox bb, float inset, float heightOffset, Span<Vector3> outPoints)
        {
            Vector3 min = bb.Min + new Vector3(inset, 0, inset);
            Vector3 max = bb.Max - new Vector3(inset, 0, inset);
            float midX = (min.X + max.X) * 0.5f;
            float midZ = (min.Z + max.Z) * 0.5f;
            float y = min.Y + heightOffset;

            outPoints[0] = new Vector3(min.X, y, min.Z);
            outPoints[1] = new Vector3(midX, y, min.Z);
            outPoints[2] = new Vector3(max.X, y, min.Z);
            outPoints[3] = new Vector3(min.X, y, midZ);
            outPoints[4] = new Vector3(midX, y, midZ);
            outPoints[5] = new Vector3(max.X, y, midZ);
            outPoints[6] = new Vector3(min.X, y, max.Z);
            outPoints[7] = new Vector3(midX, y, max.Z);
            outPoints[8] = new Vector3(max.X, y, max.Z);
        }

        /// <summary>
        /// Registers an input for this instance of the Entity. Adding the attribute alone won't do this, this function sets up the actual logic.
        /// </summary>
        /// <param name="name">Name of the input, should match the name of the <see cref="RegisterEntityInputs"/> added</param>
        /// <param name="handler">The function to be invoked upon this input being activated</param>
        protected void RegisterInputLocally(string name, InputHandler handler)
        {
            EntityInputs ??= new Dictionary<string, InputHandler>();

            if (!EntityInputs.TryAdd(name, handler))
                EntityInputs[name] += handler;
        }

        /// <summary>
        /// Registers an input for this instance of the Entity. Adding the attribute alone won't do this, this function sets up the actual logic.
        /// </summary>
        /// <param name="name">Name of the input, should match the name of the <see cref="RegisterEntityInputs"/> added</param>
        /// <param name="func">The function to be invoked upon this input being activated</param>
        protected void RegisterInputLocally(string name, Action<string, WorldEntity> func)
        {
            RegisterInputLocally(name, (param, from, passVars) =>
            {
                func(param, from);
                return null;
            });
        }

        /// <summary>
        /// Calls a specific output.
        /// </summary>
        /// <param name="name">Name of the output</param>
        /// <param name="data">Data to pass, if any</param>
        public void CallOutput(string name, WorldEntity from, params string[] passVariables)
        {
            if (EntityOutputs == null || !EntityOutputs.TryGetValue(name, out var functionOutputs)) return;

            var currentVars = passVariables;
            for (int i = 0; i < functionOutputs.Length; i++)
            {
                currentVars = HandleOutput(functionOutputs[i], from, currentVars);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex PassVarToken =
            new System.Text.RegularExpressions.Regex(
                @"!([A-Za-z_][A-Za-z0-9_]*)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static void TryParseOutputScript(string key, ref EntityOutput output)
        {
            if (output.ScriptSource == null) return;
            try
            {
                output.Script = new EXParser().Parse(new EXScriptTokenizer().Tokenize(output.ScriptSource));
            }
            catch (Exception ex)
            {
                Logger.AppendError($"Failed to parse output {key} script: {ex}");
                output.Script = null;
            }
        }

        private async void RunOutputProgram(EntityOutput output, WorldEntity from, Box<string[]> vars)
        {
            try
            {
                if (output.Delay > 0) await new QueuedDelay(this, output.Delay);
                vars.Value = await CastStep(output, from, vars.Value);

                for (int i = 0; i < output.Refire; i++)
                {
                    if (output.Delay > 0) await new QueuedDelay(this, output.Delay);
                    vars.Value = await CastStep(output, from, vars.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.AppendError($"Output chain on '{Name}' failed: {ex.Message}");
            }
        }

        private static string[] CastOnce(EntityOutput output, WorldEntity from, string[] currentVars)
        {
            var targets = EntityManager.FindEntityIndexByName(output.EntityTarget);
            if (output.EntityTarget == "_activator")
                targets = new[] { EntityManager.entities.FindIndex(e => e == from) };
            if (targets == null) return currentVars;

            var accumulated = currentVars;

            foreach (int index in targets)
            {
                if (index < 0) continue;

                var parameter = output.InputParameters;
                if (!string.IsNullOrEmpty(parameter) && parameter.Contains('!'))
                {
                    bool missingVar = false;
                    parameter = PassVarToken.Replace(parameter, match =>
                    {
                        string varName = match.Groups[1].Value;
                        string found = Array.Find(currentVars, p => p != null && p.Split(':')[0] == varName);
                        if (string.IsNullOrEmpty(found))
                        {
                            Logger.AppendError($"PassVariables did not contain '{varName}'!");
                            missingVar = true;
                            return match.Value;
                        }
                        int colonIdx = found.IndexOf(':');
                        return colonIdx >= 0 ? found.Substring(colonIdx + 1) : found;
                    });
                    if (missingVar) continue;
                }

                var contributed = EntityManager.entities[index].CallInput(output.EntityInputTarget, parameter, from, currentVars);
                accumulated = MergePassVariables(accumulated, contributed);
            }

            return accumulated;
        }
        private Task<string[]> CastStep(EntityOutput output, WorldEntity from, string[] currentVars)
        {
            if (output.Script != null)
                return RunScriptStep(output, from, currentVars);

            return Task.FromResult(CastOnce(output, from, currentVars));
        }
        
        private async Task<string[]> RunScriptStep(EntityOutput output, WorldEntity from, string[] currentVars)
        {
            var chainBox = new Box<string[]> { Value = currentVars };
            var host = new WorldEntityHost(this, from, chainBox);
            var handle = EXRuntime.Run(output.Script, host);

            liveIOScripts.Add(handle);
            await handle.Completion;
            liveIOScripts.Remove(handle);

            return chainBox.Value;
        }

        private string[] HandleOutput(EntityOutput output, WorldEntity from, string[] passVariables)
        {
            var vars = new Box<string[]> { Value = passVariables };
            RunOutputProgram(output, from, vars);
            return vars.Value;
        }

        /// <summary>
        /// Merges two pass variable arrays. Entries in <paramref name="contributed"/>
        /// overwrite entries with the same key in <paramref name="existing"/>.
        /// Returns <paramref name="existing"/> unchanged if <paramref name="contributed"/>
        /// is null or empty.
        /// </summary>
        public static string[] MergePassVariables(string[] existing, string[] contributed)
        {
            if (contributed == null || contributed.Length == 0) return existing;
            if (existing == null || existing.Length == 0) return contributed;

            // Build a dictionary keyed by variable name for fast dedup.
            var dict = new Dictionary<string, string>(existing.Length + contributed.Length);

            foreach (var v in existing)
                if (v != null) dict[v.Split(':')[0]] = v;

            foreach (var v in contributed)
                if (v != null) dict[v.Split(':')[0]] = v; // contributed wins on collision

            return dict.Values.ToArray();
        }

        /// <summary>
        /// Calls a specific input.
        /// </summary>
        /// <param name="name">Name of the input</param>
        /// <param name="data">Data passed along, if any</param>
        public string[] CallInput(string name, string data, WorldEntity from, string[] passVariables = null)
        {
            if (EntityInputs == null ||
                !EntityInputs.TryGetValue(name, out var handler) ||
                handler == null) return null;

            // Each subscribed handler can contribute variables.
            // Accumulate them across all handlers on this input.
            string[] accumulated = null;
            foreach (InputHandler h in handler.GetInvocationList().Cast<InputHandler>())
            {
                var result = h.Invoke(data, from, passVariables ?? Array.Empty<string>());
                accumulated = MergePassVariables(accumulated, result);
            }
            return accumulated;
        }

        public object ReadProperty(string propertyName, EntityPropertyType readAs)
        {
            if (properties == null)
            {
                return null;
            }

            EntityProperty property = Array.Find(properties, prop => prop.Name == propertyName);

            try
            {
                switch (readAs)
                {
                    default:

                        return property.Value;
                    case EntityPropertyType.Color:

                        if (property.Value == null)
                            return new Color();

                        string[] colors = property.Value.Split(',');
                        return new Color(byte.Parse(colors[0]), byte.Parse(colors[1]), byte.Parse(colors[2]), (byte)255);

                    case EntityPropertyType.Position:
                    case EntityPropertyType.Direction:

                        if (property.Value == null)
                            return new Vector3();

                        string[] vectors = property.Value.Split(',');
                        return new Vector3(float.Parse(vectors[0], CultureInfo.InvariantCulture), float.Parse(vectors[1], CultureInfo.InvariantCulture), float.Parse(vectors[2], CultureInfo.InvariantCulture));

                    case EntityPropertyType.Float:

                        if (property.Value == null)
                            return 0f;

                        return float.Parse(property.Value, CultureInfo.InvariantCulture);

                    case EntityPropertyType.Bool:

                        if (property.Value == null)
                            return false;

                        return property.Value == "1";
                }
            }
            catch
            {
                return null;
            }
        }
        public CustomSaveData CaptureCustomData()
        {
            var data = Controller?.CaptureCustomData() ?? new CustomSaveData();

            data.vals ??= new Dictionary<string, object>();

            // Save all values
            foreach (var (key, accessor) in SaveValueCache.GetCache(GetType()))
                data.vals[key] = accessor.Getter(this);

            // Capture the controller's members too
            if (Controller != null)
                foreach (var (key, accessor) in SaveValueCache.GetCache(Controller.GetType()))
                    data.vals[key] = accessor.Getter(Controller);

            data.vals["conditions"] = conditions.ToArray().Select(i => ConditionManager.GetID(i.GetType())).ToArray();

            return data;
        }
        public void RestoreCustomData(CustomSaveData? o)
        {
            Controller?.RestoreCustomData(o);

            if (o == null || o.Value.vals == null) return;

            var accessors = SaveValueCache.GetCache(GetType());

            // Restore this entity's members
            foreach (var (key, accessor) in SaveValueCache.GetCache(GetType()))
            {
                if (!o.Value.vals.TryGetValue(key, out var value)) continue;
                try { accessor.Setter(this, Convert.ChangeType(value, accessor.ValueType)); }
                catch { }
            }

            // Restore the controller's members too
            if (Controller != null)
            {
                foreach (var (key, accessor) in SaveValueCache.GetCache(Controller.GetType()))
                {
                    if (!o.Value.vals.TryGetValue(key, out var value)) continue;
                    try { accessor.Setter(Controller, Convert.ChangeType(value, accessor.ValueType)); }
                    catch { }
                }
            }

            if (o.Value.vals.TryGetValue("conditions", out var val))
            {
                int[] conds = val switch
                {
                    int[] arr => arr,                                      // already correct (no JSON roundtrip)
                    long[] arr => arr.Select(x => (int)x).ToArray(),       // JSON default integer type
                    Newtonsoft.Json.Linq.JArray ja => ja.ToObject<int[]>(),  // JSON.NET JArray
                    _ => null
                };

                if (conds != null)
                {
                    foreach (int c in conds)
                    {
                        var type = ConditionManager.GetType(c);
                        if (type != null)
                            AddCondition(Activator.CreateInstance(type) as ICondition);
                    }
                }
            }
        }

        public Vector3 GetRandomPointOnEntityBounds()
        {
            var corners = AxisAlignedBox ? Bounds.GetCorners() : OrientedBounds.GetCorners();

            Matrix t = AxisAlignedBox ? Matrix.Identity : OrientedBounds.Transformation;
            Vector3 center = AxisAlignedBox ? (Bounds.Max + Bounds.Min) * 0.5f : OrientedBounds.Center;
            Vector3 axisX = new Vector3(t.M11, t.M12, t.M13);
            Vector3 axisY = new Vector3(t.M21, t.M22, t.M23);
            Vector3 axisZ = new Vector3(t.M31, t.M32, t.M33);

            switch (PhysicsShape)
            {
                case EntityPhysicsShapes.Cylinder:
                    return RandomPointOnCylinder(this, center, axisX, axisY, axisZ);

                default:
                    return RandomPointOnOBB(this, center, axisX, axisY, axisZ);
            }
        }
        private static Vector3 RandomPointOnOBB(WorldEntity entity, Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            BoundingBox b = entity.GetRealBounds();
            Vector3 scale = entity.Scale;

            Vector3 min = b.Min * scale;
            Vector3 max = b.Max * scale;
            Vector3 half = (max - min) * 0.5f;

            Vector3 normX = Vector3.Normalize(axisX);
            Vector3 normY = Vector3.Normalize(axisY);
            Vector3 normZ = Vector3.Normalize(axisZ);

            float sx = half.X;
            float sy = half.Y;
            float sz = half.Z;

            float faceXY = sx * sy;
            float faceXZ = sx * sz;
            float faceYZ = sy * sz;
            float total = 2f * (faceXY + faceXZ + faceYZ);

            float u = (float)Random.Shared.NextDouble() * 2f - 1f;
            float v = (float)Random.Shared.NextDouble() * 2f - 1f;
            float side = (Random.Shared.NextDouble() < 0.5) ? -1f : 1f;
            float pick = (float)Random.Shared.NextDouble() * total;

            if (pick < 2f * faceYZ)
                return center + side * sx * normX + u * sy * normY + v * sz * normZ;
            else if (pick < 2f * (faceYZ + faceXZ))
                return center + u * sx * normX + side * sy * normY + v * sz * normZ;
            else
                return center + u * sx * normX + v * sy * normY + side * sz * normZ;
        }
        private static Vector3 RandomPointOnCylinder(WorldEntity e, Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            BoundingBox b = e.GetRealBounds();
            Vector3 scale = e.Scale;

            Vector3 min = b.Min * scale;
            Vector3 max = b.Max * scale;
            Vector3 half = (max - min) * 0.5f;

            float halfHeight = half.Y;
            float radius = Math.Max(half.X, half.Z);

            Vector3 up = Vector3.Normalize(axisY);
            Vector3 radialX = Vector3.Normalize(axisX);
            Vector3 radialZ = Vector3.Normalize(axisZ);

            float capArea = MathF.PI * radius * radius;
            float sideArea = 2f * MathF.PI * radius * (halfHeight * 2f);
            float total = 2f * capArea + sideArea;

            float angle = (float)Random.Shared.NextDouble() * MathF.Tau;
            float pick = (float)Random.Shared.NextDouble() * total;

            float cosA = MathF.Cos(angle);
            float sinA = MathF.Sin(angle);

            if (pick < 2f * capArea)
            {
                float r = radius * MathF.Sqrt((float)Random.Shared.NextDouble());
                float side = (pick < capArea) ? -1f : 1f;
                Vector3 disk = r * (cosA * radialX + sinA * radialZ);
                return center + side * halfHeight * up + disk;
            }
            else
            {
                float t = (float)Random.Shared.NextDouble() * 2f - 1f;
                return center
                     + t * halfHeight * up
                     + radius * cosA * radialX
                     + radius * sinA * radialZ;
            }
        }
    }
    public abstract class EntityController
    {
        public WorldEntity entity;
        public abstract void OnSpawn();
        public abstract void OnDespawn();
        public abstract void OnUpdate(GameTime gameTime);
        public abstract void OnRender(GameTime gameTime);
        /// <summary>
        /// Runs before the first update, but after all entities have properly been loaded and spawned in.
        /// </summary>
        public virtual void OnAllEntitiesSpawned() { }
        public virtual void OnBeforeRender(GameTime gameTime) { }
        public virtual void OnCollision(Vector3 oldVelocity) { }
        public virtual void OnTriggerCollideWithEntity(WorldEntity other) { }
        public virtual void OnCollideWithEntity(WorldEntity other) { }
        public virtual CustomSaveData CaptureCustomData() { return new CustomSaveData(); }
        public virtual void RestoreCustomData(CustomSaveData? o) { }
        public virtual void OnTakeDamage(DamageInfo info) { }
        public virtual void OnContactCreated(Vector3 point) { }

        public virtual bool ShouldRayCollide(Ray ray) => true;

        /// <summary>
        /// This is called once per class on game load. Any "static" data should be loaded here.
        /// </summary>
        public virtual void Prefetch() { }

        /// <summary>
        /// Runs a provided function after a certain delay, 0 being 1 frame.
        /// </summary>
        protected void Defer(Action doWhat, float delay)
        {
            entity?.queuedTasks.Add((delay, doWhat));
        }
    }
    public struct CustomSaveData
    {
        public Dictionary<string, object> vals;
    }
    public struct DamageInfo
    {
        public WorldEntity from;
        public Vector3? hitLocation;
        public int damage;
        public int damageType;
    }
}
