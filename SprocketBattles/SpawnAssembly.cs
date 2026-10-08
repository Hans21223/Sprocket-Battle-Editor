using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

/// A vehicle's connected physics assembly. Captured before placement, its valid relative poses can also put it
/// back during the short spawn-recovery window without leaving a connected body or a world anchor behind.
internal sealed class SpawnAssembly
{
    internal Transform Root { get; }
    internal IReadOnlyList<Rigidbody> Bodies => bodies;
    internal IReadOnlyList<Collider> Colliders => colliders;
    internal bool HasHealthyPose => healthy != null;
    internal string? Failure { get; private set; }

    readonly string label;
    readonly List<Rigidbody> bodies = new();
    readonly List<Collider> colliders = new();
    readonly List<Joint> worldJoints = new();
    readonly List<Transform> intermediateTransforms = new();
    readonly HashSet<IntPtr> bodyPointers = new();
    VehicleBehaviour? owner;
    Snapshot? healthy;

    sealed class BodyPose
    {
        internal Rigidbody Body = null!;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal int Depth;
    }

    sealed class AnchorPose
    {
        internal Joint Joint = null!;
        internal Vector3 Position;
    }

    sealed class TransformPose
    {
        internal Transform Transform = null!;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal int Depth;
    }

    sealed class Snapshot
    {
        internal Vector3 Origin;
        internal Quaternion Rotation;
        internal readonly List<BodyPose> Bodies = new();
        internal readonly List<AnchorPose> Anchors = new();
        internal readonly List<TransformPose> Transforms = new();
    }

    SpawnAssembly(Transform root, string label) { Root = root; this.label = label; }

    /// Scene joints are supplied once by the caller, rather than searched for again for every vehicle.
    internal static SpawnAssembly Capture(Transform root, Il2CppArrayBase<Joint> joints, string? id = null)
    {
        var assembly = new SpawnAssembly(root, id ?? "vehicle");
        try { assembly.FindBodies(joints); assembly.CaptureHealthy(); }
        catch (Exception ex) { assembly.Fail($"could not capture its physics assembly: {ex.Message}"); }
        return assembly;
    }

    void FindBodies(Il2CppArrayBase<Joint> joints)
    {
        if (Root == null) { Fail("its root was destroyed before placement"); return; }
        owner = Root.GetComponentInParent<VehicleBehaviour>() ?? Root.GetComponentInChildren<VehicleBehaviour>(true);
        var pending = new Queue<Rigidbody>();
        void Add(Rigidbody body)
        {
            if (!bodyPointers.Add(body.Pointer)) return;
            bodies.Add(body); pending.Enqueue(body);
        }
        foreach (var body in Root.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body == null) continue;
            if (!Belongs(body))
            {
                // Moving the root would move this body's transform too, even if it were excluded from the list.
                Fail("another vehicle is parented under its root; moving it would also move that vehicle");
                return;
            }
            Add(body);
        }
        if (bodies.Count == 0) { Fail("no vehicle rigidbodies were found under its root"); return; }

        var links = new Dictionary<IntPtr, List<(Joint Joint, Rigidbody Own, Rigidbody? Other)>>();
        void Link(IntPtr pointer, (Joint Joint, Rigidbody Own, Rigidbody? Other) link)
        {
            if (!links.TryGetValue(pointer, out var list)) links[pointer] = list = new();
            list.Add(link);
        }
        foreach (var joint in joints)
        {
            if (joint == null || joint.gameObject.scene.handle != Root.gameObject.scene.handle) continue;
            var own = joint.GetComponent<Rigidbody>();
            if (own == null) continue;
            var other = joint.connectedBody;
            Link(own.Pointer, (joint, own, other));
            if (other != null && other.Pointer != own.Pointer) Link(other.Pointer, (joint, own, other));
        }
        var anchors = new HashSet<IntPtr>();
        while (pending.Count > 0)
        {
            var body = pending.Dequeue();
            if (!links.TryGetValue(body.Pointer, out var list)) continue;
            foreach (var link in list)
            {
                if (link.Other == null)
                {
                    if (link.Own.Pointer == body.Pointer && anchors.Add(link.Joint.Pointer)) worldJoints.Add(link.Joint);
                    continue;
                }
                var next = link.Own.Pointer == body.Pointer ? link.Other : link.Own;
                if (next == null) continue;
                if (!Belongs(next))
                {
                    Fail("a vehicle joint connects to another vehicle or an outside ancestor; moving only one side is unsafe");
                    return;
                }
                Add(next);
            }
        }

        // A non-body transform between two controlled bodies (or a body and the root) can itself become NaN.
        // Keep those parents too, so a valid child world pose never has to be set through an invalid matrix.
        var controlled = new HashSet<IntPtr>(bodies.Select(b => b.transform.Pointer)) { Root.Pointer };
        var intermediates = new HashSet<IntPtr>();
        foreach (var body in bodies)
        {
            var chain = new List<Transform>();
            var parent = body.transform.parent;
            while (parent != null && !controlled.Contains(parent.Pointer)) { chain.Add(parent); parent = parent.parent; }
            if (parent == null) continue; // an outside body's unrelated ancestors remain untouched
            foreach (var transform in chain)
                if (intermediates.Add(transform.Pointer)) intermediateTransforms.Add(transform);
        }

        var seenColliders = new HashSet<IntPtr>();
        void AddColliders(Il2CppArrayBase<Collider> found)
        {
            foreach (var collider in found)
            {
                if (collider == null || !SameOwner(collider.transform)) continue;
                var attached = collider.attachedRigidbody;
                if (attached != null && !bodyPointers.Contains(attached.Pointer)) continue;
                if (seenColliders.Add(collider.Pointer)) colliders.Add(collider);
            }
        }
        AddColliders(Root.GetComponentsInChildren<Collider>(true));
        foreach (var body in bodies) AddColliders(body.GetComponentsInChildren<Collider>(true));
    }

    bool SameOwner(Transform transform)
    {
        var vehicle = transform.GetComponentInParent<VehicleBehaviour>();
        if (vehicle != null && (owner == null || vehicle.Pointer != owner.Pointer)) return false;
        // Some gateways carry their behaviour below the body's object rather than on an ancestor.
        foreach (var child in transform.GetComponentsInChildren<VehicleBehaviour>(true))
            if (child != null && (owner == null || child.Pointer != owner.Pointer)) return false;
        return true;
    }

    bool Belongs(Rigidbody body) => body != null
        && body.gameObject.scene.handle == Root.gameObject.scene.handle
        && (body.transform.Pointer == Root.Pointer || !Root.IsChildOf(body.transform)) // never an ancestor of the root
        && SameOwner(body.transform);

    void CaptureHealthy()
    {
        if (Failure != null) return;
        if (!TrySnapshot(out var current)) return;
        var inverse = Quaternion.Inverse(current!.Rotation);
        var saved = new Snapshot { Origin = Vector3.zero, Rotation = Quaternion.identity };
        foreach (var pose in current.Bodies)
        {
            var position = inverse * (pose.Position - current.Origin);
            if (!Finite(position) || !Rotation(inverse * pose.Rotation, out var rotation))
            { Fail("its body poses exceed the valid coordinate range; no safe recovery snapshot was saved"); return; }
            saved.Bodies.Add(new BodyPose { Body = pose.Body, Depth = pose.Depth, Position = position, Rotation = rotation });
        }
        foreach (var anchor in current.Anchors)
        {
            var position = inverse * (anchor.Position - current.Origin);
            if (!Finite(position)) { Fail("its anchors exceed the valid coordinate range; no safe recovery snapshot was saved"); return; }
            saved.Anchors.Add(new AnchorPose { Joint = anchor.Joint, Position = position });
        }
        foreach (var pose in current.Transforms)
        {
            var position = inverse * (pose.Position - current.Origin);
            if (!Finite(position) || !Rotation(inverse * pose.Rotation, out var rotation))
            { Fail("its parent poses exceed the valid coordinate range; no safe recovery snapshot was saved"); return; }
            saved.Transforms.Add(new TransformPose { Transform = pose.Transform, Depth = pose.Depth, Position = position, Rotation = rotation });
        }
        healthy = saved;
    }

    bool TrySnapshot(out Snapshot? snapshot)
    {
        snapshot = null;
        if (Root == null || !Finite(Root.position) || !Rotation(Root.rotation, out var rotation))
            return Fail("its root has no valid pose; a healthy spawn snapshot is required for recovery");
        var next = new Snapshot { Origin = Root.position, Rotation = rotation };
        foreach (var body in bodies)
        {
            if (body == null || !Belongs(body)) return Fail("a captured body was destroyed or changed vehicle ownership");
            var transform = body.transform;
            var position = transform.position;
            if (!Finite(position) || !Rotation(transform.rotation, out var turn))
                return Fail("a body has an invalid transform; moving an invalid pose would spread it to the assembly");
            next.Bodies.Add(new BodyPose { Body = body, Position = position, Rotation = turn, Depth = Depth(transform) });
        }
        foreach (var joint in worldJoints)
        {
            if (joint == null) continue; // a broken joint is not rebuilt by spawn recovery
            if (joint.connectedBody != null) return Fail("a captured world joint changed its connection");
            if (!Finite(joint.connectedAnchor)) return Fail("a world joint has an invalid anchor");
            next.Anchors.Add(new AnchorPose { Joint = joint, Position = joint.connectedAnchor });
        }
        foreach (var transform in intermediateTransforms)
        {
            if (transform == null || !Finite(transform.position) || !Rotation(transform.rotation, out var turn))
                return Fail("an intermediate parent has no valid transform; the healthy spawn snapshot is required");
            next.Transforms.Add(new TransformPose { Transform = transform, Position = transform.position, Rotation = turn, Depth = Depth(transform) });
        }
        snapshot = next;
        return true;
    }

    /// Current poses are all read before changing a transform. Parents are then applied before their children.
    internal bool Move(Vector3 to, Quaternion turn)
    {
        if (!HasHealthyPose) return Fail("no healthy spawn pose was captured; the vehicle was left untouched");
        return TrySnapshot(out var snapshot) && Apply(snapshot!, to, turn);
    }

    /// Restore the initially captured local poses, not positions read from a tank that has already become NaN.
    internal bool TryRestore(Vector3 to, Quaternion turn)
    {
        if (healthy == null) return Fail("no healthy spawn pose was captured; the vehicle cannot be restored safely");
        return Apply(healthy, to, turn);
    }

    bool Apply(Snapshot source, Vector3 to, Quaternion turn)
    {
        if (Root == null || !Finite(to) || !Rotation(turn, out turn)) return Fail("the requested placement pose is invalid");
        var turnBy = turn * Quaternion.Inverse(source.Rotation);
        Vector3 Moved(Vector3 at) => to + turnBy * (at - source.Origin);
        // Calculate and validate every destination before any Unity setter runs.
        var destinations = new List<BodyPose>();
        foreach (var pose in source.Bodies)
        {
            if (pose.Body == null || !Belongs(pose.Body)) return Fail("a captured body was destroyed or changed vehicle ownership");
            var position = Moved(pose.Position);
            if (!Finite(position) || !Rotation(turnBy * pose.Rotation, out var rotation))
                return Fail("a body destination is invalid; the vehicle was left untouched");
            destinations.Add(new BodyPose { Body = pose.Body, Position = position, Rotation = rotation, Depth = Depth(pose.Body.transform) });
        }
        var anchors = new List<AnchorPose>();
        foreach (var pose in source.Anchors)
        {
            if (pose.Joint == null) continue;
            if (pose.Joint.connectedBody != null) return Fail("a captured world joint changed its connection");
            var position = Moved(pose.Position);
            if (!Finite(position)) return Fail("a world-anchor destination is invalid; the vehicle was left untouched");
            anchors.Add(new AnchorPose { Joint = pose.Joint, Position = position });
        }
        var transforms = new List<TransformPose>();
        foreach (var pose in source.Transforms)
        {
            if (pose.Transform == null || !SameOwner(pose.Transform)) return Fail("a captured parent was destroyed or changed vehicle ownership");
            var position = Moved(pose.Position);
            if (!Finite(position) || !Rotation(turnBy * pose.Rotation, out var rotation))
                return Fail("an intermediate-parent destination is invalid; the vehicle was left untouched");
            transforms.Add(new TransformPose { Transform = pose.Transform, Position = position, Rotation = rotation, Depth = Depth(pose.Transform) });
        }
        var controlled = new HashSet<IntPtr>(destinations.Select(p => p.Body.transform.Pointer)) { Root.Pointer };
        foreach (var pose in transforms) controlled.Add(pose.Transform.Pointer);
        foreach (var transform in destinations.Select(p => p.Body.transform).Append(Root))
        {
            var parent = transform.parent;
            if (parent != null && !controlled.Contains(parent.Pointer)
                && (!Finite(parent.position) || !Rotation(parent.rotation, out _)))
                return Fail("an outside parent has an invalid pose; it cannot safely be moved as part of this vehicle");
        }

        try
        {
            Root.SetPositionAndRotation(to, turn);
            // Interleave body and non-body parents by depth; setting all intermediate parents first would move
            // an intermediate transform again when its Rigidbody ancestor is subsequently positioned.
            var ordered = transforms.Select(p => (p.Transform, p.Position, p.Rotation, p.Depth, Body: (Rigidbody?)null))
                .Concat(destinations.Select(p => (Transform: p.Body.transform, p.Position, p.Rotation, p.Depth, Body: (Rigidbody?)p.Body)))
                .OrderBy(p => p.Depth);
            foreach (var pose in ordered)
            {
                pose.Transform.SetPositionAndRotation(pose.Position, pose.Rotation);
                if (pose.Body != null) { pose.Body.position = pose.Position; pose.Body.rotation = pose.Rotation; }
            }
            foreach (var anchor in anchors) anchor.Joint.connectedAnchor = anchor.Position;
            foreach (var pose in destinations) Settle(pose.Body);
            Failure = null;
            return true;
        }
        catch (Exception ex) { return Fail($"placing the physics assembly failed: {ex.Message}"); }
    }

    void Settle(Rigidbody body)
    {
        // A blueprint's mass and valid custom centre/inertia are never replaced. Kinematic velocity setters are
        // unsupported by Unity. Mass properties still need validation before the main body becomes dynamic.
        bool broken = (!body.isKinematic && (!Finite(body.velocity) || !Finite(body.angularVelocity))) || !Finite(body.centerOfMass)
            || !Inertia(body.inertiaTensor) || !Rotation(body.inertiaTensorRotation, out _);
        if (!body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        if (!Finite(body.centerOfMass)) body.ResetCenterOfMass();
        if (!Inertia(body.inertiaTensor) || !Rotation(body.inertiaTensorRotation, out _)) body.ResetInertiaTensor();
        if (broken) Trace.Write($"placing: {label} reset invalid physics from its healthy spawn pose");
    }

    internal bool HasInvalidPhysics
    {
        get
        {
            if (Root == null) return false; // destruction is not a physics recovery request
            if (!Finite(Root.position) || !Rotation(Root.rotation, out _)) return true;
            foreach (var transform in intermediateTransforms)
                if (transform != null && (!Finite(transform.position) || !Rotation(transform.rotation, out _))) return true;
            foreach (var body in bodies)
            {
                if (body == null) continue;
                if (!Finite(body.position) || !Rotation(body.rotation, out _)
                    || !Finite(body.transform.position) || !Rotation(body.transform.rotation, out _)) return true;
                if ((!body.isKinematic && (!Finite(body.velocity) || !Finite(body.angularVelocity)))
                    || !Finite(body.centerOfMass) || !Inertia(body.inertiaTensor) || !Rotation(body.inertiaTensorRotation, out _)) return true;
            }
            return worldJoints.Any(j => j != null && j.connectedBody == null && !Finite(j.connectedAnchor));
        }
    }

    internal bool TryGetBounds(out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
            var next = collider.bounds;
            if (!Finite(next.center) || !Finite(next.extents)) continue;
            if (!found) { bounds = next; found = true; }
            else bounds.Encapsulate(next);
        }
        return found;
    }

    static int Depth(Transform transform)
    {
        int depth = 0;
        for (var parent = transform.parent; parent != null; parent = parent.parent) depth++;
        return depth;
    }

    internal static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    // Unity uses a zero component for an axis with infinite inertia; keep that valid game/custom setting.
    static bool Inertia(Vector3 value) => Finite(value) && value.x >= 0 && value.y >= 0 && value.z >= 0;
    static bool Rotation(Quaternion value, out Quaternion normalized)
    {
        normalized = default;
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z) || !float.IsFinite(value.w)) return false;
        double length = Math.Sqrt((double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z + (double)value.w * value.w);
        if (!double.IsFinite(length) || length < 1e-8) return false;
        normalized = new Quaternion((float)(value.x / length), (float)(value.y / length), (float)(value.z / length), (float)(value.w / length));
        return true;
    }

    bool Fail(string why)
    {
        if (Failure != why) Trace.Write($"placing: {label}: {why}");
        Failure = why;
        return false;
    }
}
