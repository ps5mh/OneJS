using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace OneJS {
    /// <summary>
    /// Batched vector drawing: replays a flat numeric command buffer onto a
    /// MeshGenerationContext's Painter2D in a single JS->C# crossing.
    ///
    /// Without this, every Painter2D op (BeginPath/MoveTo/LineTo/Fill/...) plus
    /// every `new Vector2`/`new Color` argument inside an onGenerateVisualContent
    /// callback is its own reflection crossing. A path with N segments costs
    /// ~2N+ crossings per repaint, which is the dominant cost of custom vector
    /// drawing on the QuickJS interpreter.
    ///
    /// This mirrors what StyleBridge.ApplyStyles did for styles: the JS-side
    /// Painter recorder packs all ops into one Float32Array, sends it once, and
    /// C# replays them here with direct typed calls: zero reflection, with the
    /// Vector2/Color structs constructed C#-side.
    ///
    /// Buffer format: a self-describing opcode stream
    ///   [opcode, ...args, opcode, ...args, ...]
    /// The opcode integers and their arg layouts are the contract shared with
    /// onejs-react's painter.ts. Keep the two in sync.
    ///
    /// Called from JS via:
    ///   CS.OneJS.PainterBridge.Execute(mgc, floatBuffer)
    /// </summary>
    public static class PainterBridge {
        // Opcode contract: must match JSModules/onejs-react/src/painter.ts, in
        // both the numbers below and the operand counts each case arm consumes.
        // PainterOpcodeContractTests in the container compares the two files and
        // fails on either kind of drift; a stride mismatch has no other symptom
        // than a drawing that comes out subtly wrong.
        const int OpBeginPath = 1;
        const int OpClosePath = 2;
        const int OpMoveTo = 3;
        const int OpLineTo = 4;
        const int OpArc = 5;
        const int OpArcTo = 6;
        const int OpBezierCurveTo = 7;
        const int OpQuadraticCurveTo = 8;
        const int OpFill = 9;
        const int OpStroke = 10;
        const int OpLineWidth = 11;
        const int OpFillColor = 12;
        const int OpStrokeColor = 13;
        const int OpLineCap = 14;
        const int OpLineJoin = 15;
        const int OpMiterLimit = 16;
        const int OpDashOffset = 17;
        const int OpDashPattern = 18;

        public static void Execute(MeshGenerationContext mgc, object bufferObj) {
            if (mgc == null || bufferObj == null) return;

            // The buffer arrives as the {__csArray, __csArrayType:"float"} marker
            // (a Float32Array on the JS side). Reuse the shared conversion that
            // StyleBridge relies on so we get a plain float[].
            var buffer = QuickJSNative.ConvertToTargetType(bufferObj, typeof(float[])) as float[];
            if (buffer == null || buffer.Length == 0) return;

            var p = mgc.painter2D;
            int n = buffer.Length;
            int i = 0;
#if !UNITY_6000_0_OR_NEWER
            // Painter2D has no dash support below Unity 6000.0: emulated here by
            // walking MoveTo/LineTo segments and splitting them into on/off
            // sub-segments. Arcs/curves are not dashed (still drawn solid) -
            // dashing those would need the same walk over a curved path length,
            // which isn't implemented yet.
            float dashOn = 0f, dashOff = 0f, dashPhase = 0f;
            Vector2 currentPos = Vector2.zero;
#endif
            try {
                while (i < n) {
                    int op = (int)buffer[i++];
                    switch (op) {
                        case OpBeginPath: p.BeginPath(); break;
                        case OpClosePath: p.ClosePath(); break;
                        case OpMoveTo: {
                            var pos = new Vector2(buffer[i], buffer[i + 1]);
                            p.MoveTo(pos);
#if !UNITY_6000_0_OR_NEWER
                            currentPos = pos;
                            dashPhase = 0f;
#endif
                            i += 2; break;
                        }
                        case OpLineTo: {
                            var target = new Vector2(buffer[i], buffer[i + 1]);
#if !UNITY_6000_0_OR_NEWER
                            if (dashOn > 0f && dashOff > 0f) {
                                DrawDashedSegment(p, ref currentPos, target, dashOn, dashOff, ref dashPhase);
                            } else {
                                p.LineTo(target);
                                currentPos = target;
                            }
#else
                            p.LineTo(target);
#endif
                            i += 2; break;
                        }
                        case OpArc:
                            p.Arc(new Vector2(buffer[i], buffer[i + 1]), buffer[i + 2],
                                Angle.Radians(buffer[i + 3]), Angle.Radians(buffer[i + 4]),
                                MapArcDirection(buffer[i + 5]));
                            i += 6; break;
                        case OpArcTo:
                            p.ArcTo(new Vector2(buffer[i], buffer[i + 1]),
                                new Vector2(buffer[i + 2], buffer[i + 3]), buffer[i + 4]);
                            i += 5; break;
                        case OpBezierCurveTo:
                            p.BezierCurveTo(new Vector2(buffer[i], buffer[i + 1]),
                                new Vector2(buffer[i + 2], buffer[i + 3]),
                                new Vector2(buffer[i + 4], buffer[i + 5]));
                            i += 6; break;
                        case OpQuadraticCurveTo:
                            p.QuadraticCurveTo(new Vector2(buffer[i], buffer[i + 1]),
                                new Vector2(buffer[i + 2], buffer[i + 3]));
                            i += 4; break;
                        case OpFill:
                            p.Fill(MapFillRule(buffer[i])); i += 1; break;
                        case OpStroke: p.Stroke(); break;
                        case OpLineWidth: p.lineWidth = buffer[i]; i += 1; break;
                        case OpFillColor:
                            p.fillColor = new Color(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
                            i += 4; break;
                        case OpStrokeColor:
                            p.strokeColor = new Color(buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
                            i += 4; break;
                        case OpLineCap: p.lineCap = MapLineCap(buffer[i]); i += 1; break;
                        case OpLineJoin: p.lineJoin = MapLineJoin(buffer[i]); i += 1; break;
                        case OpMiterLimit: p.miterLimit = buffer[i]; i += 1; break;
                        case OpDashOffset:
#if UNITY_6000_0_OR_NEWER
                            p.dashOffset = buffer[i];
#else
                            dashPhase += buffer[i];
#endif
                            i += 1; break;
                        case OpDashPattern:
#if UNITY_6000_0_OR_NEWER
                            p.SetDashPattern(buffer[i], buffer[i + 1]);
#else
                            dashOn = buffer[i];
                            dashOff = buffer[i + 1];
#endif
                            i += 2; break;
                        default:
                            Debug.LogWarning(
                                $"[PainterBridge] Unknown opcode {op} at index {i - 1}; aborting buffer.");
                            return;
                    }
                }
            } catch (Exception ex) {
                // A correct recorder never emits a truncated buffer; this guards
                // against corruption without tearing down the whole repaint.
                OneJSLog.Exception($"[PainterBridge] Execute failed near index {i}", ex);
            }
        }

#if !UNITY_6000_0_OR_NEWER
        // Walks from `from` to `to`, splitting the straight segment into on/off
        // sub-segments of length dashOn/dashOff. `phase` is the distance already
        // travelled along the current dash cycle, carried across calls so a
        // multi-segment path dashes continuously instead of restarting at every
        // LineTo. Draws via MoveTo/LineTo pairs rather than relying on any
        // native dash support.
        static void DrawDashedSegment(Painter2D p, ref Vector2 from, Vector2 to, float dashOn, float dashOff, ref float phase) {
            float period = dashOn + dashOff;
            var delta = to - from;
            float total = delta.magnitude;
            if (total <= 1e-6f) { from = to; return; }
            var dir = delta / total;

            float posInPeriod = phase % period;
            if (posInPeriod < 0f) posInPeriod += period;
            bool penDown = posInPeriod < dashOn;
            if (penDown) p.MoveTo(from);

            float traveled = 0f;
            while (traveled < total - 1e-6f) {
                float distToBoundary = penDown ? (dashOn - posInPeriod) : (period - posInPeriod);
                float step = Mathf.Min(distToBoundary, total - traveled);
                traveled += step;
                var point = from + dir * traveled;

                if (penDown) p.LineTo(point);

                posInPeriod += step;
                if (posInPeriod >= period - 1e-6f) posInPeriod = 0f;

                if (step >= distToBoundary - 1e-6f && traveled < total - 1e-6f) {
                    penDown = !penDown;
                    if (penDown) p.MoveTo(point);
                }
            }

            phase += total;
            from = to;
        }
#endif

        // JS sends stable opcode-local enum ints (see painter.ts); map them to
        // Unity enums explicitly so the buffer contract does not depend on Unity's
        // enum underlying values.
        static ArcDirection MapArcDirection(float v) =>
            (int)v == 1 ? ArcDirection.CounterClockwise : ArcDirection.Clockwise;

        static FillRule MapFillRule(float v) =>
            (int)v == 1 ? FillRule.OddEven : FillRule.NonZero;

        static LineCap MapLineCap(float v) =>
            (int)v == 1 ? LineCap.Round : LineCap.Butt;

        static LineJoin MapLineJoin(float v) {
            switch ((int)v) {
                case 1: return LineJoin.Bevel;
                case 2: return LineJoin.Round;
                default: return LineJoin.Miter;
            }
        }
    }
}
