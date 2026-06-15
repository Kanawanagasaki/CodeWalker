using SharpDX;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// Expression Virtual Machine — interprets expression bytecode to produce
    /// facial bone transforms. Integrated into the rendering pipeline so that
    /// .yed expressions actually animate ped faces instead of being ignored.
    /// </summary>
    public class ExpressionVm
    {
        /// <summary>Computation stack (Vec4 values).</summary>
        public List<Vector4> Stack { get; } = new List<Vector4>();

        /// <summary>Variable storage: variable hash → value.</summary>
        public Dictionary<uint, Vector4> Variables { get; } = new Dictionary<uint, Vector4>();

        /// <summary>Track output: (BoneId, Track, ComponentIndex) → value.</summary>
        public Dictionary<(ushort BoneId, byte Track, byte CompIdx), Vector4> Tracks { get; } = new Dictionary<(ushort, byte, byte), Vector4>();

        /// <summary>Track validity: (BoneId, Track) → bool.</summary>
        public Dictionary<(ushort BoneId, byte Track), bool> TrackValidity { get; } = new Dictionary<(ushort, byte), bool>();

        /// <summary>
        /// Set of track keys that were WRITTEN by TrackSet/TrackSetOffset/TrackSetComp/
        /// TrackSetBoneTransform instructions during execution. These are the VM's OUTPUT
        /// tracks — only these should be applied to the skeleton. Seeded input tracks
        /// (which the VM reads via TrackGet but never writes) are NOT in this set.
        /// Without this distinction, seeded body bone rotations get applied as output,
        /// causing hips/arms to spin.
        /// </summary>
        public HashSet<(ushort BoneId, byte Track, byte CompIdx)> OutputTracks { get; } = new HashSet<(ushort, byte, byte)>();

        /// <summary>Program counter (instruction index in current stream).</summary>
        public int PC { get; set; }

        /// <summary>Current stream index being executed.</summary>
        public int StreamIndex { get; set; } = -1;

        /// <summary>Current expression.</summary>
        public Expression CurrentExpression { get; set; }

        /// <summary>Current stream being executed.</summary>
        public ExpressionStream CurrentStream { get; set; }

        /// <summary>Playback time for PushTime instruction.</summary>
        public float Time { get; set; }

        /// <summary>Frame delta time for PushDeltaTime instruction.</summary>
        public float DeltaTime { get; set; } = 1f / 30f;

        /// <summary>Whether the VM reached the End instruction.</summary>
        public bool Halted { get; set; }

        /// <summary>Spring simulation state preserved across frames.</summary>
        public class SpringState
        {
            public Vector4 Position;
            public Vector4 Velocity;
        }

        public Dictionary<int, SpringState> SpringStates { get; } = new Dictionary<int, SpringState>();

        /// <summary>Maximum steps to prevent infinite loops.</summary>
        public int MaxSteps { get; set; } = 10000;

        /// <summary>Expression weight (0.0 = no effect, 1.0 = full).</summary>
        public float Weight { get; set; } = 1.0f;

        private int _totalSteps;

        // ── Stack helpers ──────────────────────────────────────────────

        public void Push(Vector4 v) => Stack.Add(v);

        public Vector4 Pop()
        {
            if (Stack.Count == 0)
            {
                // Instead of throwing (which kills the entire stream),
                // return zero and let execution continue. A single bad
                // instruction shouldn't prevent all subsequent ones from running.
                return Vector4.Zero;
            }
            var val = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            return val;
        }

        public Vector4 Peek()
        {
            if (Stack.Count == 0) return Vector4.Zero;
            return Stack[Stack.Count - 1];
        }

        // ── Initialization ─────────────────────────────────────────────

        /// <summary>
        /// Initialize the VM for a specific expression and stream.
        /// Resets stack, PC, and output tracks. Preserves spring states across frames.
        /// </summary>
        public void Init(Expression expression, int streamIndex = 0, float time = 0f, float deltaTime = 1f / 30f)
        {
            Stack.Clear();
            PC = 0;
            Halted = false;
            CurrentExpression = expression;
            StreamIndex = streamIndex;
            Time = time;
            DeltaTime = deltaTime;
            Variables.Clear();
            Tracks.Clear();
            TrackValidity.Clear();
            OutputTracks.Clear();
            // Note: SpringStates are NOT cleared — they persist across frames for smooth simulation
            _totalSteps = 0;

            if (expression.Streams?.data_items == null || streamIndex >= expression.Streams.data_items.Length)
            {
                CurrentStream = null;
                return;
            }

            CurrentStream = expression.Streams.data_items[streamIndex];

            // Initialize variables from expression's variable hashes (all zero)
            if (expression.Variables?.data_items != null)
            {
                foreach (var hash in expression.Variables.data_items)
                    Variables[hash.Hash] = Vector4.Zero;
            }

            // Set Expression_Weight variable if present
            SetWeightVariable();

            // Initialize tracks from expression's track list
            int trackCount = 0;
            int faceTrackCount = 0;
            if (expression.Tracks?.data_items != null)
            {
                foreach (var track in expression.Tracks.data_items)
                {
                    var key = (track.BoneId, track.Track, (byte)0);
                    if (!Tracks.ContainsKey(key))
                    {
                        // Quaternion format defaults to identity, others to zero
                        Tracks[key] = (track.Format == 1) ? new Vector4(0, 0, 0, 1) : Vector4.Zero;
                    }
                    TrackValidity[(track.BoneId, track.Track)] = true;
                    trackCount++;
                    // In the expression system, Track=0/1/2 are FORMAT indicators (pos/rot/scale).
                    // All expression tracks that remap through BoneTracksDict are potential face tracks.
                    // The old check (Track >= 24) was for animation track types, not expression tracks.
                    if (expression.BoneTracksDict != null)
                    {
                        var lookupKey = new ExpressionTrack() { BoneId = track.BoneId, Track = track.Track, Flags = track.Format };
                        if (expression.BoneTracksDict.ContainsKey(lookupKey))
                            faceTrackCount++;
                    }
                }
            }
        }

        /// <summary>Re-init for a new frame, keeping spring state.</summary>
        public void ResetForFrame(float time, float deltaTime)
        {
            Stack.Clear();
            PC = 0;
            Halted = false;
            Time = time;
            DeltaTime = deltaTime;
            Tracks.Clear();
            TrackValidity.Clear();
            OutputTracks.Clear();
            _totalSteps = 0;

            if (CurrentExpression == null) return;

            // Re-initialize tracks from expression's track list with identity/zero defaults
            if (CurrentExpression.Tracks?.data_items != null)
            {
                foreach (var track in CurrentExpression.Tracks.data_items)
                {
                    var key = (track.BoneId, track.Track, (byte)0);
                    if (!Tracks.ContainsKey(key))
                    {
                        Tracks[key] = (track.Format == 1) ? new Vector4(0, 0, 0, 1) : Vector4.Zero;
                    }
                    TrackValidity[(track.BoneId, track.Track)] = true;
                }
            }

            // Update weight variable
            SetWeightVariable();
        }

        /// <summary>
        /// Seed the VM's track dictionary with current skeleton bone transforms.
        /// This MUST be called after ResetForFrame but before RunAllStreams.
        /// TrackGet instructions need to read the current bone state — without seeding,
        /// they return zero/identity, causing the VM to produce garbage output that
        /// collapses all face vertices.
        /// </summary>
        /// <param name="boneTransforms">Dictionary mapping (BoneId, Track) → Vector4 transform value</param>
        public void SeedTracks(Dictionary<(ushort BoneId, byte Track), Vector4> boneTransforms)
        {
            if (boneTransforms == null) return;
            foreach (var kvp in boneTransforms)
            {
                var key = (kvp.Key.BoneId, kvp.Key.Track, (byte)0);
                // Overwrite the zero/identity defaults with actual bone data
                Tracks[key] = kvp.Value;
            }
        }

        private void SetWeightVariable()
        {
            // The expression weight variable is typically hashed as "Expression_Weight"
            // Common hash: 0x7EF6F26E (JenkHash of "Expression_Weight")
            // We set it in the Variables dict if the expression uses it
            uint weightHash = 0x7EF6F26E;
            if (Variables.ContainsKey(weightHash))
            {
                Variables[weightHash] = new Vector4(Weight, 0, 0, 0);
            }
            // Also try lowercase variant
            uint weightHash2 = 0x4D4C5E1E; // common alt hash
            if (Variables.ContainsKey(weightHash2))
            {
                Variables[weightHash2] = new Vector4(Weight, 0, 0, 0);
            }
        }

        // ── Execution ──────────────────────────────────────────────────

        /// <summary>Execute all instructions in the current stream until halt or error.</summary>
        public void RunAll()
        {
            while (!Halted)
            {
                _totalSteps++;
                if (_totalSteps > MaxSteps) break;

                if (CurrentStream?.Instructions == null) break;
                if (PC < 0 || PC >= CurrentStream.Instructions.Length) break;

                var instr = CurrentStream.Instructions[PC];
                try
                {
                    ExecuteInstruction(instr);
                }
                catch (Exception ex)
                {
                    // Log non-fatal: the VM continues executing subsequent instructions.
                    // Stack underflows no longer throw (Pop returns Vector4.Zero instead),
                    // but other unexpected errors are caught here so the stream can continue.
                    Debug.WriteLine($"  EXCEPTION at stream={StreamIndex} pc={PC} instr={instr.Type}: {ex.Message}");
                    // Don't break — try to continue executing remaining instructions
                }

                if (!Halted)
                {
                    PC++;
                    if (PC >= CurrentStream.Instructions.Length)
                        Halted = true;
                }
            }
        }

        /// <summary>Execute all streams of the expression sequentially.</summary>
        public void RunAllStreams(float time, float deltaTime)
        {
            if (CurrentExpression?.Streams?.data_items == null)
                return;

            for (int s = 0; s < CurrentExpression.Streams.data_items.Length; s++)
            {
                StreamIndex = s;
                CurrentStream = CurrentExpression.Streams.data_items[s];
                if (CurrentStream?.Instructions == null)
                    continue;

                Stack.Clear();
                PC = 0;
                Halted = false;
                _totalSteps = 0;

                // NOTE: We do NOT push the expression weight onto the stack here.
                // In the RAGE expression VM, the blend weight is a VM-level property
                // that Blend instructions access internally — it is NOT on the stack.
                // The stack is for computational results (e.g., a Blend pushes its
                // output, then TrackSet pops it). Pushing Weight here caused the
                // first Blend to consume it, leaving subsequent Blends with an empty
                // stack → stack underflow → entire stream dies after 3 instructions.

                // Do NOT clear Tracks between streams!
                // Tracks accumulate across streams — stream N may need to read
                // output written by stream N-1 via TrackGet.

                RunAll();
            }

            // Summarize output
            int trackSetCount = 0;
            int faceTrackSetCount = 0;
            foreach (var kvp in Tracks)
            {
                trackSetCount++;
                // Expression tracks use Track=0/1/2 as FORMAT (pos/rot/scale), not 24/25/26.
                // Count as "face track" if the track has a BoneTracksDict mapping (meaning it's
                // an expression-internal bone that maps to a skeleton face bone).
                if (CurrentExpression?.BoneTracksDict != null)
                {
                    var fmt = byte.MaxValue;
                    // Try to get format from expression tracks
                    if (CurrentExpression.Tracks?.data_items != null)
                    {
                        foreach (var et in CurrentExpression.Tracks.data_items)
                        {
                            if (et.BoneId == kvp.Key.BoneId && et.Track == kvp.Key.Track)
                            {
                                fmt = et.Format;
                                break;
                            }
                        }
                    }
                    if (fmt != byte.MaxValue)
                    {
                        var lookupKey = new ExpressionTrack() { BoneId = kvp.Key.BoneId, Track = kvp.Key.Track, Flags = fmt };
                        if (CurrentExpression.BoneTracksDict.ContainsKey(lookupKey))
                            faceTrackSetCount++;
                    }
                }
            }
        }

        // ── Instruction dispatch ───────────────────────────────────────

        private void ExecuteInstruction(ExpressionInstrBase instr)
        {
            switch (instr.Type)
            {
                // ── Stack manipulation ──────────────────────
                case ExpressionInstrType.End:
                    Halted = true;
                    break;

                case ExpressionInstrType.Pop:
                    Pop();
                    break;

                case ExpressionInstrType.Dup:
                    Push(Peek());
                    break;

                case ExpressionInstrType.Push0:
                    Push(new Vector4(0, 0, 0, 0));
                    break;

                case ExpressionInstrType.Push1:
                    Push(new Vector4(1, 0, 0, 0));
                    break;

                case ExpressionInstrType.PushFloat:
                    {
                        var pf = (ExpressionInstrFloat)instr;
                        Push(new Vector4(pf.Value, 0, 0, 0));
                    }
                    break;

                case ExpressionInstrType.PushVector:
                    {
                        var pv = (ExpressionInstrVector)instr;
                        Push(pv.Value);
                    }
                    break;

                case ExpressionInstrType.PushTime:
                    Push(new Vector4(Time, 0, 0, 0));
                    break;

                case ExpressionInstrType.PushDeltaTime:
                    Push(new Vector4(DeltaTime, 0, 0, 0));
                    break;

                case ExpressionInstrType.ToVector:
                    {
                        // Pop 3 scalar values from the stack and assemble into a Vector3.
                        // The stack order is: X pushed first, Y second, Z third (top).
                        // So we pop Z, then Y, then X.
                        // This is used before FromEuler to create Euler angle vectors:
                        //   TrackGetOffsetComp × weight  →  X component
                        //   TrackGetOffsetComp × weight  →  Y component
                        //   TrackGetOffsetComp × weight  →  Z component
                        //   ToVector                      →  (X, Y, Z, 0)
                        //   FromEuler                     →  quaternion
                        var z = Pop().X;
                        var y = Pop().X;
                        var x = Pop().X;
                        Push(new Vector4(x, y, z, 0));
                    }
                    break;

                // ── Track operations ────────────────────────
                case ExpressionInstrType.TrackGet:
                case ExpressionInstrType.TrackGetOffset:
                    ExecuteTrackGet(instr, fullVector: true);
                    break;

                case ExpressionInstrType.TrackGetComp:
                case ExpressionInstrType.TrackGetOffsetComp:
                    ExecuteTrackGetComp(instr);
                    break;

                case ExpressionInstrType.TrackGetBoneTransform:
                    ExecuteTrackGetBoneTransform(instr);
                    break;

                case ExpressionInstrType.TrackValid:
                    ExecuteTrackValid(instr);
                    break;

                case ExpressionInstrType.Unk23:
                    // Unknown track operation — treat as no-op
                    Pop(); // consume track reference
                    Push(Vector4.Zero);
                    break;

                case ExpressionInstrType.TrackSet:
                    ExecuteTrackSet(instr, fullVector: true, isOffset: false);
                    break;

                case ExpressionInstrType.TrackSetOffset:
                    ExecuteTrackSet(instr, fullVector: true, isOffset: true);
                    break;

                case ExpressionInstrType.TrackSetComp:
                    ExecuteTrackSetComp(instr, isOffset: false);
                    break;

                case ExpressionInstrType.TrackSetOffsetComp:
                    ExecuteTrackSetComp(instr, isOffset: true);
                    break;

                case ExpressionInstrType.TrackSetBoneTransform:
                    ExecuteTrackSetBoneTransform(instr);
                    break;

                // ── Variable operations ─────────────────────
                case ExpressionInstrType.GetVariable:
                    ExecuteGetVariable(instr);
                    break;

                case ExpressionInstrType.SetVariable:
                    ExecuteSetVariable(instr);
                    break;

                // ── Jump operations ──────────────────────────
                case ExpressionInstrType.Jump:
                    {
                        var j = (ExpressionInstrJump)instr;
                        PC = j.Index + (int)j.Data3Offset;
                        // Don't auto-advance PC after this
                        return;
                    }

                case ExpressionInstrType.JumpIfTrue:
                    {
                        var j = (ExpressionInstrJump)instr;
                        var cond = Pop();
                        if (cond.X != 0)
                        {
                            PC = j.Index + (int)j.Data3Offset;
                            return;
                        }
                    }
                    break;

                case ExpressionInstrType.JumpIfFalse:
                    {
                        var j = (ExpressionInstrJump)instr;
                        var cond = Pop();
                        if (cond.X == 0)
                        {
                            PC = j.Index + (int)j.Data3Offset;
                            return;
                        }
                    }
                    break;

                // ── Unary math ──────────────────────────────
                case ExpressionInstrType.VectorAbs:
                    {
                        var a = Pop();
                        Push(new Vector4(Math.Abs(a.X), Math.Abs(a.Y), Math.Abs(a.Z), Math.Abs(a.W)));
                    }
                    break;

                case ExpressionInstrType.VectorNeg:
                    {
                        var a = Pop();
                        Push(-a);
                    }
                    break;

                case ExpressionInstrType.VectorNeg3:
                    {
                        var a = Pop();
                        Push(new Vector4(-a.X, -a.Y, -a.Z, a.W));
                    }
                    break;

                case ExpressionInstrType.VectorRcp:
                    {
                        var a = Pop();
                        Push(new Vector4(
                            a.X != 0 ? 1f / a.X : 0,
                            a.Y != 0 ? 1f / a.Y : 0,
                            a.Z != 0 ? 1f / a.Z : 0,
                            a.W != 0 ? 1f / a.W : 0));
                    }
                    break;

                case ExpressionInstrType.VectorSqrt:
                    {
                        var a = Pop();
                        Push(new Vector4((float)Math.Sqrt(a.X), (float)Math.Sqrt(a.Y), (float)Math.Sqrt(a.Z), (float)Math.Sqrt(a.W)));
                    }
                    break;

                case ExpressionInstrType.VectorSquare:
                    {
                        var a = Pop();
                        Push(new Vector4(a.X * a.X, a.Y * a.Y, a.Z * a.Z, a.W * a.W));
                    }
                    break;

                case ExpressionInstrType.VectorDeg2Rad:
                    {
                        var a = Pop();
                        float d = (float)(Math.PI / 180.0);
                        Push(new Vector4(a.X * d, a.Y * d, a.Z * d, a.W * d));
                    }
                    break;

                case ExpressionInstrType.VectorRad2Deg:
                    {
                        var a = Pop();
                        float d = (float)(180.0 / Math.PI);
                        Push(new Vector4(a.X * d, a.Y * d, a.Z * d, a.W * d));
                    }
                    break;

                case ExpressionInstrType.VectorSaturate:
                    {
                        var a = Pop();
                        Push(new Vector4(
                            Math.Max(0, Math.Min(1, a.X)),
                            Math.Max(0, Math.Min(1, a.Y)),
                            Math.Max(0, Math.Min(1, a.Z)),
                            Math.Max(0, Math.Min(1, a.W))));
                    }
                    break;

                // ── Binary math ─────────────────────────────
                case ExpressionInstrType.VectorAdd:
                    {
                        var b = Pop(); var a = Pop();
                        Push(a + b);
                    }
                    break;

                case ExpressionInstrType.VectorSub:
                    {
                        var b = Pop(); var a = Pop();
                        Push(a - b);
                    }
                    break;

                case ExpressionInstrType.VectorMul:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W));
                    }
                    break;

                case ExpressionInstrType.VectorMin:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z), Math.Min(a.W, b.W)));
                    }
                    break;

                case ExpressionInstrType.VectorMax:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z), Math.Max(a.W, b.W)));
                    }
                    break;

                case ExpressionInstrType.QuatMul:
                    {
                        var b = Pop(); var a = Pop();
                        var qResult = Quaternion.Normalize(Quaternion.Multiply(
                            new Quaternion(a.X, a.Y, a.Z, a.W),
                            new Quaternion(b.X, b.Y, b.Z, b.W)));
                        Push(new Vector4(qResult.X, qResult.Y, qResult.Z, qResult.W));
                    }
                    break;

                // ── Comparison ──────────────────────────────
                case ExpressionInstrType.VectorGreaterThan:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X > b.X ? 1f : 0f, a.Y > b.Y ? 1f : 0f, a.Z > b.Z ? 1f : 0f, a.W > b.W ? 1f : 0f));
                    }
                    break;

                case ExpressionInstrType.VectorLessThan:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X < b.X ? 1f : 0f, a.Y < b.Y ? 1f : 0f, a.Z < b.Z ? 1f : 0f, a.W < b.W ? 1f : 0f));
                    }
                    break;

                case ExpressionInstrType.VectorGreaterEqual:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X >= b.X ? 1f : 0f, a.Y >= b.Y ? 1f : 0f, a.Z >= b.Z ? 1f : 0f, a.W >= b.W ? 1f : 0f));
                    }
                    break;

                case ExpressionInstrType.VectorLessEqual:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X <= b.X ? 1f : 0f, a.Y <= b.Y ? 1f : 0f, a.Z <= b.Z ? 1f : 0f, a.W <= b.W ? 1f : 0f));
                    }
                    break;

                case ExpressionInstrType.VectorEqual:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X == b.X ? 1f : 0f, a.Y == b.Y ? 1f : 0f, a.Z == b.Z ? 1f : 0f, a.W == b.W ? 1f : 0f));
                    }
                    break;

                case ExpressionInstrType.VectorNotEqual:
                    {
                        var b = Pop(); var a = Pop();
                        Push(new Vector4(a.X != b.X ? 1f : 0f, a.Y != b.Y ? 1f : 0f, a.Z != b.Z ? 1f : 0f, a.W != b.W ? 1f : 0f));
                    }
                    break;

                // ── Ternary / interpolation ─────────────────
                case ExpressionInstrType.VectorClamp:
                    {
                        var max = Pop(); var min = Pop(); var v = Pop();
                        Push(new Vector4(
                            Math.Max(min.X, Math.Min(max.X, v.X)),
                            Math.Max(min.Y, Math.Min(max.Y, v.Y)),
                            Math.Max(min.Z, Math.Min(max.Z, v.Z)),
                            Math.Max(min.W, Math.Min(max.W, v.W))));
                    }
                    break;

                case ExpressionInstrType.VectorLerp:
                    {
                        var t = Pop(); var b = Pop(); var a = Pop();
                        Push(new Vector4(
                            a.X + (b.X - a.X) * t.X,
                            a.Y + (b.Y - a.Y) * t.Y,
                            a.Z + (b.Z - a.Z) * t.Z,
                            a.W + (b.W - a.W) * t.W));
                    }
                    break;

                case ExpressionInstrType.VectorMad:
                    {
                        var c = Pop(); var b = Pop(); var a = Pop();
                        // a * b + c (multiply-add)
                        Push(new Vector4(
                            a.X * b.X + c.X,
                            a.Y * b.Y + c.Y,
                            a.Z * b.Z + c.Z,
                            a.W * b.W + c.W));
                    }
                    break;

                case ExpressionInstrType.QuatSlerp:
                    {
                        var t = Pop(); var b = Pop(); var a = Pop();
                        var qa = new Quaternion(a.X, a.Y, a.Z, a.W);
                        var qb = new Quaternion(b.X, b.Y, b.Z, b.W);
                        // Ensure both quaternions are in the same hemisphere for correct Slerp.
                        // If qa.W < 0, negate qa (same rotation, different representation).
                        // Then check if qb is in the opposite hemisphere and negate if needed.
                        if (qa.W < 0) qa = new Quaternion(-qa.X, -qa.Y, -qa.Z, -qa.W);
                        if (Quaternion.Dot(qa, qb) < 0) qb = new Quaternion(-qb.X, -qb.Y, -qb.Z, -qb.W);
                        var qr = Quaternion.Slerp(qa, qb, t.X);
                        Push(new Vector4(qr.X, qr.Y, qr.Z, qr.W));
                    }
                    break;

                // ── Euler ───────────────────────────────────
                case ExpressionInstrType.FromEuler:
                    {
                        var e = Pop(); // radians
                        var q = Quaternion.RotationYawPitchRoll(e.Y, e.X, e.Z);
                        // Ensure consistent hemisphere (W >= 0) to prevent flipping
                        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                        Push(new Vector4(q.X, q.Y, q.Z, q.W));
                    }
                    break;

                case ExpressionInstrType.ToEuler:
                    {
                        var v = Pop();
                        var q = new Quaternion(v.X, v.Y, v.Z, v.W);
                        // Extract Euler angles from quaternion
                        float sinr = 2f * (q.W * q.X + q.Y * q.Z);
                        float cosr = 1f - 2f * (q.X * q.X + q.Y * q.Y);
                        float roll = (float)Math.Atan2(sinr, cosr);
                        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
                        float pitch = Math.Abs(sinp) >= 1f ? (float)(Math.PI / 2) * Math.Sign(sinp) : (float)Math.Asin(sinp);
                        float siny = 2f * (q.W * q.Z + q.X * q.Y);
                        float cosy = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
                        float yaw = (float)Math.Atan2(siny, cosy);
                        Push(new Vector4(roll, pitch, yaw, 0));
                    }
                    break;

                // ── Transform ───────────────────────────────
                case ExpressionInstrType.VectorTransform:
                    {
                        // Vector transform — pop matrix and vector, apply
                        // For now, treat as no-op (3x4 matrix from stack)
                        // This is used rarely and requires 3 stack values for the matrix
                        var v = Pop(); // vector
                        var m2 = Pop(); // matrix row 2
                        var m1 = Pop(); // matrix row 1
                        var m0 = Pop(); // matrix row 0
                        Push(new Vector4(
                            m0.X * v.X + m0.Y * v.Y + m0.Z * v.Z + m0.W,
                            m1.X * v.X + m1.Y * v.Y + m1.Z * v.Z + m1.W,
                            m2.X * v.X + m2.Y * v.Y + m2.Z * v.Z + m2.W,
                            v.W));
                    }
                    break;

                // ── Blend ───────────────────────────────────
                case ExpressionInstrType.BlendVector:
                    ExecuteBlend(instr, isQuaternion: false);
                    break;

                case ExpressionInstrType.BlendQuaternion:
                    ExecuteBlend(instr, isQuaternion: true);
                    break;

                // ── Spring ──────────────────────────────────
                case ExpressionInstrType.DefineSpring:
                    ExecuteDefineSpring(instr);
                    break;

                // ── LookAt ──────────────────────────────────
                case ExpressionInstrType.LookAt:
                    ExecuteLookAt(instr);
                    break;

                default:
                    // Unknown opcode — skip
                    break;
            }
        }

        // ── Track operation implementations ──────────────────────────

        private ExpressionTrack GetTrack(ushort trackIndex)
        {
            if (CurrentExpression?.Tracks?.data_items == null || trackIndex >= CurrentExpression.Tracks.data_items.Length)
                return default;
            return CurrentExpression.Tracks.data_items[trackIndex];
        }

        private void ExecuteTrackGet(ExpressionInstrBase instr, bool fullVector)
        {
            var bone = (ExpressionInstrBone)instr;
            var key = (bone.BoneId, bone.Track, (byte)0);
            Vector4 val;
            if (Tracks.TryGetValue(key, out val))
            {
                if (bone.UseDefaults && val.LengthSquared() < 0.0001f)
                {
                    // Use default based on format
                    val = (bone.Format == 1) ? new Vector4(0, 0, 0, 1) : Vector4.Zero;
                }
            }
            else
            {
                val = (bone.Format == 1) ? new Vector4(0, 0, 0, 1) : Vector4.Zero;
            }
            Push(val);
        }

        private void ExecuteTrackGetComp(ExpressionInstrBase instr)
        {
            var bone = (ExpressionInstrBone)instr;
            var key = (bone.BoneId, bone.Track, bone.ComponentIndex);
            Vector4 val;
            float comp = Tracks.TryGetValue(key, out val) ? val.X :
                         Tracks.TryGetValue((bone.BoneId, bone.Track, (byte)0), out val) ? GetComponent(val, bone.ComponentIndex) : 0f;
            Push(new Vector4(comp, 0, 0, 0));
        }

        private void ExecuteTrackGetBoneTransform(ExpressionInstrBase instr)
        {
            var bone = (ExpressionInstrBone)instr;
            // Get both rotation (track 1) and translation (track 0) for the bone
            var rotKey = (bone.BoneId, (byte)1, (byte)0);
            var posKey = (bone.BoneId, (byte)0, (byte)0);
            Vector4 rot, pos;
            if (!Tracks.TryGetValue(rotKey, out rot)) rot = new Vector4(0, 0, 0, 1);
            if (!Tracks.TryGetValue(posKey, out pos)) pos = Vector4.Zero;
            // Push rotation, then translation (7 floats)
            Push(rot);
            Push(pos);
        }

        private void ExecuteTrackValid(ExpressionInstrBase instr)
        {
            var bone = (ExpressionInstrBone)instr;
            bool valid = TrackValidity.ContainsKey((bone.BoneId, bone.Track));
            Push(new Vector4(valid ? 1f : 0f, 0, 0, 0));
        }

        private void ExecuteTrackSet(ExpressionInstrBase instr, bool fullVector, bool isOffset)
        {
            var bone = (ExpressionInstrBone)instr;
            var val = Pop();
            var key = (bone.BoneId, bone.Track, (byte)0);

            if (isOffset)
            {
                // TrackSetOffset: ADD the offset to the existing track value.
                // For rotation (Format=1), multiply quaternions.
                // For position/scale, add vectors.
                Vector4 existing;
                if (!Tracks.TryGetValue(key, out existing))
                    existing = (bone.Format == 1) ? new Vector4(0, 0, 0, 1) : Vector4.Zero;

                if (bone.Format == 1)
                {
                    // Rotation offset: multiply existing quaternion by delta
                    var qBase = new Quaternion(existing.X, existing.Y, existing.Z, existing.W);
                    var qOff = new Quaternion(val.X, val.Y, val.Z, val.W);
                    var qResult = Quaternion.Multiply(qBase, qOff);
                    if (qResult.LengthSquared() > 0.0001f)
                        qResult = Quaternion.Normalize(qResult);
                    Tracks[key] = new Vector4(qResult.X, qResult.Y, qResult.Z, qResult.W);
                }
                else
                {
                    // Position/scale offset: add vectors
                    Tracks[key] = existing + val;
                }
            }
            else
            {
                // TrackSet: replace the track value
                Tracks[key] = val;
            }

            OutputTracks.Add(key); // mark as VM output — only these are applied to skeleton
        }

        private void ExecuteTrackSetComp(ExpressionInstrBase instr, bool isOffset)
        {
            var bone = (ExpressionInstrBone)instr;
            var compVal = Pop(); // the component value to set
            var key = (bone.BoneId, bone.Track, (byte)0);
            Vector4 existing;
            if (!Tracks.TryGetValue(key, out existing)) existing = Vector4.Zero;

            if (isOffset)
            {
                // For offset variant, ADD the component value to the existing component
                switch (bone.ComponentIndex)
                {
                    case 0: existing.X += compVal.X; break;
                    case 1: existing.Y += compVal.X; break;
                    case 2: existing.Z += compVal.X; break;
                    case 3: existing.W += compVal.X; break;
                }
            }
            else
            {
                // For set variant, REPLACE the component
                switch (bone.ComponentIndex)
                {
                    case 0: existing.X = compVal.X; break;
                    case 1: existing.Y = compVal.X; break;
                    case 2: existing.Z = compVal.X; break;
                    case 3: existing.W = compVal.X; break;
                }
            }
            Tracks[key] = existing;
            OutputTracks.Add(key); // mark as VM output
        }

        private void ExecuteTrackSetBoneTransform(ExpressionInstrBase instr)
        {
            var bone = (ExpressionInstrBone)instr;
            var pos = Pop(); // translation
            var rot = Pop(); // rotation quaternion
            var rotKey = (bone.BoneId, (byte)1, (byte)0);
            var posKey = (bone.BoneId, (byte)0, (byte)0);
            Tracks[rotKey] = rot;
            Tracks[posKey] = pos;
            OutputTracks.Add(rotKey); // mark as VM output
            OutputTracks.Add(posKey); // mark as VM output
        }

        // ── Variable operations ──────────────────────────────────────

        private void ExecuteGetVariable(ExpressionInstrBase instr)
        {
            var vi = (ExpressionInstrVariable)instr;
            Vector4 val;
            if (!Variables.TryGetValue(vi.Variable.Hash, out val)) val = Vector4.Zero;
            Push(val);
        }

        private void ExecuteSetVariable(ExpressionInstrBase instr)
        {
            var vi = (ExpressionInstrVariable)instr;
            var val = Pop();
            Variables[vi.Variable.Hash] = val;
        }

        // ── Blend operations ─────────────────────────────────────────

        /// <summary>
        /// Execute a Blend instruction (BlendVector or BlendQuaternion).
        ///
        /// The RAGE expression Blend instruction computes a per-component weighted sum:
        ///   For each source i:
        ///     1. Read ONE component value from a track: sourceValue = TrackGetComp(source[i])
        ///     2. For each output axis (X, Y, Z):
        ///        result.axis += sourceValue * weight + offset
        ///
        /// Weights, offsets, and thresholds are stored in the Values[] array, organized
        /// in groups of 4 sources. The layout for NumSourceWeights=1 (most common):
        ///   For group j = i/4, component k = i%4, base v = j * 6:
        ///     Values[v+0][k] = X weight,  Values[v+1][k] = Y weight,  Values[v+2][k] = Z weight
        ///     Values[v+3][k] = X offset,  Values[v+4][k] = Y offset,  Values[v+5][k] = Z offset
        ///
        /// For NumSourceWeights > 1, additional weight levels are stored after the base 6:
        ///   For level n (1-based), base b = v + 6 + 9*(n-1):
        ///     Values[b+0..2][k] = X/Y/Z thresholds
        ///     Values[b+3..5][k] = X/Y/Z weights
        ///     Values[b+6..8][k] = X/Y/Z offsets
        ///
        /// For BlendQuaternion, the W component is reconstructed from X,Y,Z:
        ///   W = sqrt(max(0, 1 - X² - Y² - Z²))
        /// and the final quaternion is normalized.
        /// </summary>
        private void ExecuteBlend(ExpressionInstrBase instr, bool isQuaternion)
        {
            var blend = (ExpressionInstrBlend)instr;
            if (blend.SourceInfos == null) return;

            // The blend weight is a VM-level property, NOT on the stack.
            float weightVal = Weight;

            // Accumulate per-axis weighted contributions
            float resultX = 0f, resultY = 0f, resultZ = 0f;
            int numSourceWeights = (int)blend.NumSourceWeights;
            if (numSourceWeights < 1) numSourceWeights = 1;
            int stride = 6 + 9 * (numSourceWeights - 1); // values per group of 4 sources

            for (int i = 0; i < blend.SourceInfos.Length; i++)
            {
                var srcInfo = blend.SourceInfos[i];

                // Look up the source track from the expression's track list
                var srcTrack = GetTrack(srcInfo.TrackIndex);
                if (srcTrack.BoneId == 0 && srcTrack.Track == 0)
                    continue; // invalid track reference (GetTrack returned default)

                // Read the full vector from the source track
                var srcKey = (srcTrack.BoneId, srcTrack.Track, (byte)0);
                Vector4 srcVec;
                if (!Tracks.TryGetValue(srcKey, out srcVec))
                    srcVec = Vector4.Zero; // face animation channels default to zero

                // Extract the single component value that this source reads
                int compIdx = srcInfo.ComponentOffset / 4;
                float sourceValue = GetComponent(srcVec, (byte)compIdx);

                // Compute the indices into the Values array
                // Sources are organized in groups of 4, with each group sharing
                // a block of Values entries. Within a group, each source uses a
                // different component [k] of the Vector4 values.
                int j = i / 4;     // which group
                int k = i % 4;     // which component within the Vector4
                int v = j * stride; // base index into Values[]

                if (blend.Values == null || v + 5 >= blend.Values.Length)
                    continue; // not enough data

                // Read the base weights and offsets for X, Y, Z output axes
                float xWeight = blend.Values[v + 0][k];
                float yWeight = blend.Values[v + 1][k];
                float zWeight = blend.Values[v + 2][k];
                float xOffset = blend.Values[v + 3][k];
                float yOffset = blend.Values[v + 4][k];
                float zOffset = blend.Values[v + 5][k];

                // Handle additional weight levels (NumSourceWeights > 1)
                // Each additional level adds a piecewise-linear segment:
                //   if sourceValue > threshold, add (sourceValue - threshold) * weight + offset
                for (int n = 1; n < numSourceWeights; n++)
                {
                    int m = n - 1;
                    int b = v + 6 + 9 * m;
                    if (b + 8 >= blend.Values.Length) break;

                    float xThresh = blend.Values[b + 0][k];
                    float yThresh = blend.Values[b + 1][k];
                    float zThresh = blend.Values[b + 2][k];
                    float xW2 = blend.Values[b + 3][k];
                    float yW2 = blend.Values[b + 4][k];
                    float zW2 = blend.Values[b + 5][k];
                    float xO2 = blend.Values[b + 6][k];
                    float yO2 = blend.Values[b + 7][k];
                    float zO2 = blend.Values[b + 8][k];

                    // Apply additional weight when source value exceeds threshold
                    if (sourceValue > xThresh)
                    {
                        xWeight += xW2;
                        xOffset += xO2;
                    }
                    if (sourceValue > yThresh)
                    {
                        yWeight += yW2;
                        yOffset += yO2;
                    }
                    if (sourceValue > zThresh)
                    {
                        zWeight += zW2;
                        zOffset += zO2;
                    }
                }

                // Accumulate the weighted contribution for each output axis
                resultX += sourceValue * xWeight + xOffset;
                resultY += sourceValue * yWeight + yOffset;
                resultZ += sourceValue * zWeight + zOffset;
            }

            // Build the result vector
            Vector4 result;
            if (isQuaternion)
            {
                // For quaternion blending, reconstruct W from X, Y, Z assuming unit quaternion.
                // The expression system's BlendQuaternion instruction stores the quaternion
                // imaginary components (X, Y, Z) and reconstructs the real component (W).
                // We always take the positive square root, then ensure the quaternion is in
                // the same hemisphere as identity (W > 0) to prevent sign-flip artifacts
                // when Slerp blends between identity and the result.
                float wSq = 1.0f - resultX * resultX - resultY * resultY - resultZ * resultZ;
                float resultW = (wSq > 0f) ? (float)Math.Sqrt(wSq) : 0f;

                // Apply expression weight by blending between identity and result.
                // Slerp requires both quaternions in the same hemisphere (same W sign)
                // to take the shortest path. Since identity has W=+1, we ensure
                // qResult also has W >= 0. Negating (X,Y,Z,W) → (-X,-Y,-Z,-W)
                // represents the same rotation but flips the hemisphere.
                var qResult = new Quaternion(resultX, resultY, resultZ, resultW);
                if (qResult.LengthSquared() > 0.0001f)
                {
                    qResult = Quaternion.Normalize(qResult);
                    // Ensure same hemisphere as identity for correct Slerp
                    if (qResult.W < 0) qResult = new Quaternion(-qResult.X, -qResult.Y, -qResult.Z, -qResult.W);
                    var qWeighted = Quaternion.Slerp(Quaternion.Identity, qResult, weightVal);
                    result = new Vector4(qWeighted.X, qWeighted.Y, qWeighted.Z, qWeighted.W);
                }
                else
                {
                    // Degenerate quaternion — return identity
                    result = new Vector4(0, 0, 0, 1);
                }
            }
            else
            {
                // For vector blending, apply weight directly
                result = new Vector4(resultX * weightVal, resultY * weightVal, resultZ * weightVal, 0);
            }

            Push(result);
        }

        // ── Spring simulation ────────────────────────────────────────

        private void ExecuteDefineSpring(ExpressionInstrBase instr)
        {
            var spring = (ExpressionInstrSpring)instr;
            var target = Pop(); // target position for the spring

            // Find or create spring state using the instruction index as key
            if (!SpringStates.TryGetValue(spring.Index, out var state))
            {
                state = new SpringState();
                SpringStates[spring.Index] = state;
            }

            // Simple spring simulation
            var desc = spring.SpringDescription;
            // Stiffness and damping are derived from the spring description vectors
            // Vector01 seems to contain stiffness, Vector02 damping
            float stiffness = desc.Vector01.Length() * 100f;
            float damping = desc.Vector02.Length() * 10f;

            // Clamp to reasonable ranges
            stiffness = Math.Max(0.1f, Math.Min(stiffness, 10000f));
            damping = Math.Max(0.1f, Math.Min(damping, 1000f));

            // Spring force: F = -stiffness * (position - target) - damping * velocity
            var springForce = (target - state.Position) * stiffness - state.Velocity * damping;
            state.Velocity = state.Velocity + springForce * DeltaTime;
            state.Position = state.Position + state.Velocity * DeltaTime;

            // Write output tracks
            if (spring.BoneTrackRot != 0)
            {
                var rotBytes = BitConverter.GetBytes(spring.BoneTrackRot);
                var boneId = BitConverter.ToUInt16(rotBytes, 0);
                var track = rotBytes[2];
                Tracks[(boneId, track, (byte)0)] = state.Position;
            }
            if (spring.BoneTrackPos != 0)
            {
                var posBytes = BitConverter.GetBytes(spring.BoneTrackPos);
                var boneId = BitConverter.ToUInt16(posBytes, 0);
                var track = posBytes[2];
                Tracks[(boneId, track, (byte)0)] = state.Position;
            }

            // Push result
            Push(state.Position);
        }

        // ── LookAt ────────────────────────────────────────────────────

        private void ExecuteLookAt(ExpressionInstrBase instr)
        {
            var la = (ExpressionInstrLookAt)instr;
            var forward = Pop(); // forward direction
            var up = Pop(); // up direction
            var position = Pop(); // position

            // Compute look-at quaternion
            var fwd = new Vector3(forward.X, forward.Y, forward.Z);
            var upVec = new Vector3(up.X, up.Y, up.Z);

            if (fwd.LengthSquared() < 0.0001f) fwd = Vector3.UnitZ;
            fwd = Vector3.Normalize(fwd);

            var right = Vector3.Cross(upVec, fwd);
            if (right.LengthSquared() < 0.0001f) right = Vector3.UnitX;
            right = Vector3.Normalize(right);

            upVec = Vector3.Cross(fwd, right);

            // Build rotation matrix and extract quaternion
            var matrix = new Matrix();
            matrix.Right = right;
            matrix.Up = upVec;
            matrix.Forward = fwd;
            var q = Quaternion.RotationMatrix(matrix);

            Push(new Vector4(q.X, q.Y, q.Z, q.W));
        }

        // ── Helper ────────────────────────────────────────────────────

        private static float GetComponent(Vector4 v, byte idx)
        {
            switch (idx)
            {
                case 0: return v.X;
                case 1: return v.Y;
                case 2: return v.Z;
                case 3: return v.W;
                default: return 0f;
            }
        }
    }
}
