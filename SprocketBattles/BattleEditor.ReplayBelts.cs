using Sprocket.ContinuousTracks.Instanced;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Tracks.Belts;
using UnityEngine;

namespace SprocketBattles;

public sealed partial class BattleEditor
{
    sealed class CapturedBelt
    {
        internal TrackBelt Belt = null!;
        internal TrackBeltInstancedRenderer Renderer = null!;
        internal int Variant;
        internal bool Stopped;
        internal ReplayBelt Data = new();
    }
    sealed class BeltVisual
    {
        internal ReplayBelt Data = null!;
        internal GameObject[] Segments = Array.Empty<GameObject>();
        internal int Frame = -1;
        internal float[] Before = Array.Empty<float>(), After = Array.Empty<float>();
    }
    readonly List<(TrackBeltInstancedRenderer Renderer, bool Visible)> hiddenBelts = new();
    readonly List<BeltVisual> replayBelts = new();

    bool CaptureInstancedBelt(Capture c, CapturedActor actor, TrackBelt belt, Func<Mesh, int?> geometry)
    {
        if (belt.beltRenderer?.TryCast<TrackBeltInstancedRenderer>() is not { } renderer) return false;
        try
        {
            if (renderer._transformBuffers is not { } buffers || renderer._variantCountCache is not { } counts) return false;
            for (int variant = 0; variant < Math.Min(buffers.Length, counts.Length); variant++)
            {
                int count = counts[variant];
                if (count < 1) continue;
                if (count > 2048 || c.Actors.Sum(a => a.Data.Belts.Sum(b => b.Count)) + actor.Data.Belts.Sum(b => b.Count) + count > 8192)
                { c.Data.Simplified = true; continue; }
                Mesh? mesh = null; int? index = null;
                for (int lod = 0; lod < renderer.lodCount; lod++)
                    if ((mesh = renderer.GetMeshForLOD(variant, lod)) != null && (index = geometry(mesh)) != null) break;
                if (index == null) { c.Data.Simplified = true; continue; }
                var data = new ReplayBelt { Count = count, Mesh = index.Value,
                    Surface = CaptureSurface(c, belt.segmentMaterialOverride?.Material ?? belt.segmentMaterial?.Material ?? renderer.material) };
                var captured = new CapturedBelt { Belt = belt, Renderer = renderer, Variant = variant, Data = data };
                CaptureBeltFrame(c, actor, captured, 0, true);
                if (data.Frames.Count == 0) { c.Data.Simplified = true; continue; }
                actor.Data.Belts.Add(data); actor.Belts.Add(captured);
            }
            Trace.Write($"replay belts {actor.Data.Unit}: {actor.Data.Belts.Sum(b => b.Count)} instanced segments");
            return true; // Editor segment renderers are inactive proxies when the instanced renderer owns the belt.
        }
        catch (Exception ex) { c.Data.Simplified = true; Trace.Write("replay instanced belt: " + ex.Message); return false; }
    }

    void CaptureBeltFrame(Capture c, CapturedActor actor, CapturedBelt belt, float at, bool alive)
    {
        if (belt.Stopped) return;
        try
        {
            var data = belt.Data;
            bool visible = alive && belt.Belt != null && belt.Belt.segmentsVisible;
            byte[] packed;
            if (!visible && data.Frames.Count > 0) packed = data.Frames[^1].Data;
            else
            {
                var buffer = belt.Renderer._transformBuffers[belt.Variant];
                if (buffer == null || buffer.stride < 64 || buffer.count < data.Count) throw new InvalidDataException("Missing live belt transform buffer.");
                var bytes = ReplayBuffer(buffer);
                var m = actor.Tank.transform.worldToLocalMatrix;
                var toHull = new System.Numerics.Matrix4x4(m.m00, m.m10, m.m20, m.m30, m.m01, m.m11, m.m21, m.m31,
                    m.m02, m.m12, m.m22, m.m32, m.m03, m.m13, m.m23, m.m33);
                var floats = ReplayMedia.BeltPoses(bytes, buffer.stride, data.Count, toHull);
                var raw = new byte[floats.Length * 4]; Buffer.BlockCopy(floats, 0, raw, 0, raw.Length); packed = ReplayMedia.Pack(raw);
            }
            if (data.Frames.Count > 0 && data.Frames[^1].Visible == visible && data.Frames[^1].Data.SequenceEqual(packed)) return;
            var before = data.Frames.LastOrDefault(); float hold = Math.Max(0, at - .1f);
            bool addHold = before != null && hold > before.Time + .001f;
            if (c.BeltBytes + packed.Length + (addHold ? before!.Data.Length : 0) > ReplayMedia.MaxBeltBytes
                || c.BeltDecodedBytes + data.Count * 40L * (addHold ? 2 : 1) > 512L * 1024 * 1024)
            { c.Data.Simplified = true; return; }
            if (addHold)
            {
                data.Frames.Add(new ReplayBeltFrame { Time = hold, Visible = before!.Visible, Data = before.Data });
                c.BeltBytes += before.Data.Length; c.BeltDecodedBytes += data.Count * 40L;
            }
            var frame = new ReplayBeltFrame { Time = at, Visible = visible, Data = packed };
            if (data.Frames.Count > 0 && data.Frames[^1].Time == at)
            { c.BeltBytes -= data.Frames[^1].Data.Length; data.Frames[^1] = frame; }
            else data.Frames.Add(frame);
            c.BeltBytes += packed.Length;
            c.BeltDecodedBytes += data.Count * 40L;
        }
        catch (Exception ex)
        {
            belt.Stopped = true;
            c.Data.Simplified = true;
            if (belt.Data.Frames.Count > 0 && belt.Data.Frames[^1].Visible && at > belt.Data.Frames[^1].Time)
            {
                var previous = belt.Data.Frames[^1];
                if (c.BeltBytes + previous.Data.Length <= ReplayMedia.MaxBeltBytes && c.BeltDecodedBytes + belt.Data.Count * 40L <= 512L * 1024 * 1024)
                {
                    belt.Data.Frames.Add(new ReplayBeltFrame { Time = at, Visible = false, Data = previous.Data });
                    c.BeltBytes += previous.Data.Length; c.BeltDecodedBytes += belt.Data.Count * 40L;
                }
            }
            Trace.Write("replay belt frame: " + ex.Message);
        }
    }

    void HideInstancedBelts(GameObject root)
    {
        var tank = root.GetComponentInChildren<VehicleBehaviour>(true);
        var visualRoot = tank != null ? VehicleVisualGateway(tank)?.gameObject ?? root : root;
        foreach (var part in visualRoot.GetComponentsInChildren<VehicleObject>(true))
            foreach (var component in ReplayEach(part.Components))
                if (component?.TryCast<TrackBelt>()?.beltRenderer?.TryCast<TrackBeltInstancedRenderer>() is { } renderer
                    && !hiddenBelts.Any(b => b.Renderer.Pointer == renderer.Pointer))
                { hiddenBelts.Add((renderer, renderer.visible)); renderer.visible = false; }
    }
    void RestoreInstancedBelts()
    {
        foreach (var (renderer, visible) in hiddenBelts) renderer.visible = visible;
        hiddenBelts.Clear();
    }
    void ReplayBeltMarkers(ReplayActor actor, GameObject root)
    {
        foreach (var belt in actor.Belts)
        {
            var visual = new BeltVisual { Data = belt, Segments = new GameObject[belt.Count] };
            for (int i = 0; i < belt.Count; i++)
            {
                var segment = new GameObject("Replay track segment"); segment.transform.SetParent(root.transform, false);
                segment.AddComponent<MeshFilter>().sharedMesh = replayMeshes[belt.Mesh];
                var renderer = segment.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = Enumerable.Repeat(replayMaterials[belt.Surface], replayMeshes[belt.Mesh].subMeshCount).ToArray();
                visual.Segments[i] = segment;
            }
            replayBelts.Add(visual);
        }
    }
    void ReplayBeltsAt(float time)
    {
        foreach (var visual in replayBelts)
        {
            var frames = visual.Data.Frames; int lo = 0, hi = frames.Count - 1;
            while (lo < hi) { int m = (lo + hi + 1) / 2; if (frames[m].Time <= time) lo = m; else hi = m - 1; }
            var a = frames[lo]; var b = frames[Math.Min(lo + 1, frames.Count - 1)];
            if (visual.Frame != lo)
            {
                float[] Decode(ReplayBeltFrame f)
                {
                    var bytes = ReplayMedia.Unpack(f.Data, visual.Data.Count * 40); var floats = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length); return floats;
                }
                visual.Before = Decode(a); visual.After = ReferenceEquals(a, b) ? visual.Before : Decode(b); visual.Frame = lo;
            }
            float t = a.Visible == b.Visible && b.Time > a.Time ? Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1) : 0;
            for (int i = 0; i < visual.Segments.Length; i++)
            {
                var segment = visual.Segments[i]; segment.SetActive(a.Visible); if (!a.Visible) continue;
                int o = i * 10; var p = visual.Before; var q = visual.After;
                segment.transform.localPosition = Vector3.Lerp(new Vector3(p[o], p[o+1], p[o+2]), new Vector3(q[o], q[o+1], q[o+2]), t);
                segment.transform.localRotation = Quaternion.Slerp(new Quaternion(p[o+3], p[o+4], p[o+5], p[o+6]), new Quaternion(q[o+3], q[o+4], q[o+5], q[o+6]), t);
                segment.transform.localScale = Vector3.Lerp(new Vector3(p[o+7], p[o+8], p[o+9]), new Vector3(q[o+7], q[o+8], q[o+9]), t);
            }
        }
    }
}
