using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    /// <summary>
    /// Advanced Control Flow Fixer with enhanced support for all data types
    /// and aggressive junk code removal for ConfuserEx deobfuscation
    /// </summary>
    internal class AdvancedControlFlowFixer : IBlocksDeobfuscator
    {
        public bool ExecuteIfNotModified { get; } = false;
        public List<MethodDef> NativeMethods = new List<MethodDef>();

        private readonly InstructionEmulator _instructionEmulator = new InstructionEmulator();
        private Blocks _blocks;
        private Local _switchKey;

        #region Key Calculation with Full Type Support

        private int? CalculateKey(SwitchData switchData)
        {
            var popValue = _instructionEmulator.Peek();
            if (popValue == null || popValue.IsUnknown())
                return null;

            long num = 0;
            
            // Support for Int32
            if (popValue.IsInt32())
            {
                var value = popValue as Int32Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                num = value.Value;
            }
            // Support for Int64 (long)
            else if (popValue.IsInt64())
            {
                var value = popValue as Int64Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                num = value.Value;
            }
            // Support for UInt64 (ulong)
            else if (popValue.IsUInt64())
            {
                var value = popValue as UInt64Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                num = (long)value.Value;
            }
            // Support for Real8 (double/float)
            else if (popValue.IsReal8())
            {
                var value = popValue as Real8Value;
                if (value == null || !value.IsValid)
                    return null;
                num = (long)value.Value;
            }
            // Support for Real4 (float)
            else if (popValue.IsReal4())
            {
                var value = popValue as Real4Value;
                if (value == null || !value.IsValid)
                    return null;
                num = (long)value.Value;
            }
            else
                return null;

            _instructionEmulator.Pop();

            if (switchData is NativeSwitchData nativeSwitchData)
            {
                var nativeMethod = new x86.X86Method(nativeSwitchData.NativeMethodDef, _blocks.Method.Module as ModuleDefMD);
                return nativeMethod.Execute((int)num);
            }
            
            if (switchData is NormalSwitchData normalSwitchData)
            {
                return (int)(num ^ normalSwitchData.Key.Value);
            }
            
            return null;
        }

        private int? CalculateSwitchCaseIndex(Block block, SwitchData switchData, int key)
        {
            if (switchData is NativeSwitchData)
            {
                _instructionEmulator.Push(new Int32Value(key));
                _instructionEmulator.Emulate(block.Instructions, block.SwitchData.IsKeyHardCoded ? 2 : 1, block.Instructions.Count - 1);

                var popValue = _instructionEmulator.Peek();
                _instructionEmulator.Pop();
                
                if (popValue == null || popValue.IsUnknown())
                    return null;
                    
                if (popValue.IsInt32())
                {
                    var value = popValue as Int32Value;
                    if (value == null || !value.AllBitsValid())
                        return null;
                    return value.Value;
                }
                else if (popValue.IsInt64())
                {
                    var value = popValue as Int64Value;
                    if (value == null || !value.AllBitsValid())
                        return null;
                    return (int)value.Value;
                }
                else if (popValue.IsUInt64())
                {
                    var value = popValue as UInt64Value;
                    if (value == null || !value.AllBitsValid())
                        return null;
                    return (int)value.Value;
                }
                else if (popValue.IsReal8())
                {
                    var value = popValue as Real8Value;
                    if (value == null || !value.IsValid)
                        return null;
                    return (int)value.Value;
                }
                else if (popValue.IsReal4())
                {
                    var value = popValue as Real4Value;
                    if (value == null || !value.IsValid)
                        return null;
                    return (int)value.Value;
                }
                return null;
            }
            
            if (switchData is NormalSwitchData normalSwitchData)
            {
                return key % normalSwitchData.DivisionKey;
            }
            
            return null;
        }

        #endregion

        #region Block Processing

        private void ProcessHardcodedSwitch(Block switchBlock)
        {
            var targets = switchBlock.Targets;
            _instructionEmulator.Push(new Int32Value(switchBlock.SwitchData.Key.Value));

            int? key = CalculateKey(switchBlock.SwitchData);
            if (!key.HasValue)
                throw new Exception("CRITICAL ERROR: KEY HAS NO VALUE");

            int? switchCaseIndex = CalculateSwitchCaseIndex(switchBlock, switchBlock.SwitchData, key.Value);
            if (!switchCaseIndex.HasValue)
                throw new Exception("CRITICAL ERROR: SWITCH CASE HAS NO VALUE");
            if (targets.Count <= switchCaseIndex)
                throw new Exception("CRITICAL ERROR: KEY OUT OF RANGE");

            var targetBlock = targets[switchCaseIndex.Value];
            targetBlock.SwitchData.Key = key;

            switchBlock.Instructions.Clear();
            switchBlock.ReplaceLastNonBranchWithBranch(0, targetBlock);
        }

        private void ProcessBlock(List<Block> switchCaseBlocks, Block block, Block switchBlock)
        {
            var targets = switchBlock.Targets;
            _instructionEmulator.Emulate(block.Instructions, 0, block.Instructions.Count);

            var peekValue = _instructionEmulator.Peek();
            if (peekValue == null || peekValue.IsUnknown())
                throw new Exception("CRITICAL ERROR: STACK VALUE UNKNOWN");

            int? key = CalculateKey(switchBlock.SwitchData);
            if (!key.HasValue)
                throw new Exception("CRITICAL ERROR: KEY HAS NO VALUE");

            int? switchCaseIndex = CalculateSwitchCaseIndex(switchBlock, switchBlock.SwitchData, key.Value);
            if (!switchCaseIndex.HasValue)
                throw new Exception("CRITICAL ERROR: SWITCH CASE HAS NO VALUE");
            if (targets.Count <= switchCaseIndex)
                throw new Exception("CRITICAL ERROR: KEY OUT OF RANGE");

            var targetBlock = targets[switchCaseIndex.Value];
            targetBlock.SwitchData.Key = key;

            // Aggressive junk removal
            RemoveAllObfuscationJunk(block);
            
            block.ReplaceLastNonBranchWithBranch(0, targetBlock);

            ProcessFallThroughs(switchCaseBlocks, switchBlock, targetBlock, key.Value);
            block.Processed = true;
        }

        private void ProcessTernaryBlock(List<Block> switchCaseBlocks, Block ternaryBlock, Block switchBlock)
        {
            var targets = switchBlock.Targets;

            for (int i = 0; i < 2; i++)
            {
                var sourceBlock = ternaryBlock.Sources[0];

                if (ternaryBlock.SwitchData.Key.HasValue)
                    SetLocalSwitchKey(ternaryBlock.SwitchData.Key.Value);

                _instructionEmulator.Emulate(sourceBlock.Instructions, 0, sourceBlock.Instructions.Count);
                _instructionEmulator.Emulate(ternaryBlock.Instructions, 0, ternaryBlock.Instructions.Count);

                var peekValue = _instructionEmulator.Peek();
                if (peekValue == null || peekValue.IsUnknown())
                    throw new Exception("CRITICAL ERROR: STACK VALUE UNKNOWN");

                int? key = CalculateKey(switchBlock.SwitchData);
                if (!key.HasValue)
                    throw new Exception("CRITICAL ERROR: KEY HAS NO VALUE");

                int? switchCaseIndex = CalculateSwitchCaseIndex(switchBlock, switchBlock.SwitchData, key.Value);
                if (!switchCaseIndex.HasValue)
                    throw new Exception("CRITICAL ERROR: SWITCH CASE HAS NO VALUE");
                if (targets.Count <= switchCaseIndex)
                    throw new Exception("CRITICAL ERROR: KEY OUT OF RANGE");

                var targetBlock = targets[switchCaseIndex.Value];
                targetBlock.SwitchData.Key = key;

                RemoveAllObfuscationJunk(sourceBlock);
                
                sourceBlock.ReplaceLastNonBranchWithBranch(0, targets[switchCaseIndex.Value]);

                ProcessFallThroughs(switchCaseBlocks, switchBlock, targets[switchCaseIndex.Value], key.Value);
            }

            RemoveAllObfuscationJunk(ternaryBlock);
            ternaryBlock.Processed = true;
        }

        #endregion

        #region Deobfuscation Entry Points

        public void DeobfuscateBegin(Blocks blocks)
        {
            _blocks = blocks;
            _instructionEmulator.Initialize(_blocks, true);
        }

        public bool Deobfuscate(List<Block> methodBlocks)
        {
            List<Block> switchBlocks = GetSwitchBlocks(methodBlocks);
            int modifications = 0;

            foreach (Block switchBlock in switchBlocks)
            {
                if (switchBlock.SwitchData.IsKeyHardCoded)
                {
                    ProcessHardcodedSwitch(switchBlock);
                    modifications++;
                    continue;
                }

                _switchKey = Instr.GetLocalVar(_blocks.Locals,
                    switchBlock.Instructions[switchBlock.Instructions.Count - 4]);

                if (DeobfuscateSwitchBlock(methodBlocks, switchBlock))
                    modifications++;
            }
            return modifications > 0;
        }

        private bool DeobfuscateSwitchBlock(List<Block> methodBlocks, Block switchBlock)
        {
            List<Block> switchFallThroughs = methodBlocks.FindAll(b => b.FallThrough == switchBlock);
            _instructionEmulator.Initialize(_blocks, true);

            int blocksLeft = switchFallThroughs.Count;
            int blockIndex = 0;
            int failedCount = 0;

            while (blocksLeft > 0)
            {
                if (blockIndex > switchFallThroughs.Count - 1)
                    blockIndex = 0;

                if (failedCount > switchFallThroughs.Count)
                {
                    Console.WriteLine("Some blocks couldn't be processed!");
                    break;
                }

                Block switchCaseBlock = switchFallThroughs[blockIndex];
                if (switchCaseBlock.Processed)
                {
                    blockIndex++;
                    continue;
                }

                if (NeedSwitchKey(switchCaseBlock))
                {
                    if (!switchCaseBlock.SwitchData.Key.HasValue)
                    {
                        failedCount++;
                        blockIndex++;
                        continue;
                    }
                    SetLocalSwitchKey(switchCaseBlock.SwitchData.Key.Value);
                }

                if (switchCaseBlock.IsTernary())
                {
                    ProcessTernaryBlock(switchFallThroughs, switchCaseBlock, switchBlock);
                }
                else
                {
                    ProcessBlock(switchFallThroughs, switchCaseBlock, switchBlock);
                }

                failedCount = 0;
                blocksLeft--;
                blockIndex++;
            }

            return blocksLeft < switchFallThroughs.Count;
        }

        #endregion

        #region Switch Block Detection

        public bool IsConfuserExSwitchBlock(Block block)
        {
            if (block.LastInstr.OpCode.Code != Code.Switch || ((Instruction[])block.LastInstr.Operand)?.Length == 0)
                return false;

            var instructions = block.Instructions;
            var lastIndex = instructions.Count - 1;

            if (instructions.Count < 4)
                return false;
            if (!instructions[lastIndex - 3].IsStloc())
                return false;
            if (!instructions[lastIndex - 2].IsLdcI4())
                return false;
            if (instructions[lastIndex - 1].OpCode != OpCodes.Rem_Un)
                return false;

            var nativeSwitchData = new NativeSwitchData(block);
            if (nativeSwitchData.Initialize())
            {
                block.SwitchData = nativeSwitchData;
                if (!NativeMethods.Contains(nativeSwitchData.NativeMethodDef))
                    NativeMethods.Add(nativeSwitchData.NativeMethodDef);
                return true;
            }

            var normalSwitchData = new NormalSwitchData(block);
            if (normalSwitchData.Initialize())
            {
                block.SwitchData = normalSwitchData;
                return true;
            }

            return false;
        }

        public List<Block> GetSwitchBlocks(List<Block> blocks)
        {
            List<Block> switchBlocks = new List<Block>();
            foreach (Block block in blocks)
                if (IsConfuserExSwitchBlock(block))
                    switchBlocks.Add(block);
            return switchBlocks;
        }

        #endregion

        #region Fall-Through Processing

        private readonly List<Block> _processedFallThroughs = new List<Block>();

        private void ProcessFallThroughs(List<Block> switchCaseBlocks, Block switchBlock, Block targetBlock, int switchKey)
        {
            DoProcessFallThroughs(switchCaseBlocks, switchBlock, targetBlock, switchKey);
            _processedFallThroughs.Clear();
        }

        private void DoProcessFallThroughs(List<Block> switchCaseBlocks, Block switchBlock, Block targetBlock, int switchKey)
        {
            if (_processedFallThroughs.Contains(targetBlock))
                return;
            _processedFallThroughs.Add(targetBlock);

            if (targetBlock.FallThrough == switchBlock && switchCaseBlocks.Contains(targetBlock) && !targetBlock.SwitchData.Key.HasValue)
                targetBlock.SwitchData.Key = switchKey;

            var fallThrough = targetBlock.FallThrough;
            if (fallThrough == null)
                return;

            if (fallThrough.LastInstr.OpCode != OpCodes.Ret && fallThrough != switchBlock)
                DoProcessFallThroughs(switchCaseBlocks, switchBlock, fallThrough, switchKey);

            if (targetBlock.CountTargets() > 1)
                foreach (Block targetBlockTarget in targetBlock.Targets)
                {
                    if (targetBlockTarget == switchBlock)
                        return;
                    DoProcessFallThroughs(switchCaseBlocks, switchBlock, targetBlockTarget, switchKey);
                }
        }

        #endregion

        #region Switch Key Management

        private bool NeedSwitchKey(Block block)
        {
            foreach (var instr in block.Instructions)
                if (instr.IsLdloc() && Instr.GetLocalVar(_blocks.Locals, instr) == _switchKey)
                    return true;
            return false;
        }

        private int? GetSwitchKey()
        {
            var val = _instructionEmulator.GetLocal(_switchKey);
            if (val == null || val.IsUnknown())
                return null;
                
            if (val.IsInt32())
            {
                var value = val as Int32Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                return value.Value;
            }
            else if (val.IsInt64())
            {
                var value = val as Int64Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                return (int)value.Value;
            }
            else if (val.IsUInt64())
            {
                var value = val as UInt64Value;
                if (value == null || !value.AllBitsValid())
                    return null;
                return (int)value.Value;
            }
            else if (val.IsReal8())
            {
                var value = val as Real8Value;
                if (value == null || !value.IsValid)
                    return null;
                return (int)value.Value;
            }
            else if (val.IsReal4())
            {
                var value = val as Real4Value;
                if (value == null || !value.IsValid)
                    return null;
                return (int)value.Value;
            }
            return null;
        }

        private void SetLocalSwitchKey(int key)
        {
            _instructionEmulator.SetLocal(_switchKey, new Int32Value(key));
        }

        #endregion

        #region Advanced Junk Code Removal

        /// <summary>
        /// Aggressively removes all types of obfuscation junk from a block
        /// Handles arithmetic operations, useless conversions, nop sleds, 
        /// dead code patterns, and other common obfuscation techniques
        /// </summary>
        private void RemoveAllObfuscationJunk(Block block)
        {
            if (block == null || block.Instructions == null || block.Instructions.Count == 0)
                return;

            bool modified = true;
            int maxIterations = 10; // Prevent infinite loops
            int iteration = 0;

            while (modified && iteration < maxIterations)
            {
                modified = false;
                iteration++;

                // Remove NOP instructions
                modified |= RemoveNops(block);
                
                // Remove dead arithmetic operations
                modified |= RemoveDeadArithmetic(block);
                
                // Remove useless conversions
                modified |= RemoveUselessConversions(block);
                
                // Remove dup/pop pairs
                modified |= RemoveDupPopPairs(block);
                
                // Remove push/pop pairs
                modified |= RemovePushPopPairs(block);
                
                // Remove redundant load/store patterns
                modified |= RemoveRedundantLoadStore(block);
                
                // Remove unreachable code after unconditional branches
                modified |= RemoveUnreachableCode(block);
            }
        }

        private bool RemoveNops(Block block)
        {
            bool modified = false;
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                if (block.Instructions[i].OpCode.Code == Code.Nop)
                {
                    block.Instructions.RemoveAt(i);
                    modified = true;
                }
            }
            return modified;
        }

        private bool RemoveDeadArithmetic(Block block)
        {
            bool modified = false;
            var junkCodes = new HashSet<Code>
            {
                Code.Add, Code.Sub, Code.Mul, Code.Div, Code.Rem,
                Code.And, Code.Or, Code.Xor, Code.Shl, Code.Shr, Code.Shr_Un,
                Code.Neg, Code.Not
            };

            for (int i = block.Instructions.Count - 2; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                if (junkCodes.Contains(instr.OpCode.Code))
                {
                    // Check if result is used or just discarded
                    bool isUsed = false;
                    for (int j = i + 1; j < block.Instructions.Count; j++)
                    {
                        var nextInstr = block.Instructions[j];
                        if (nextInstr.OpCode.FlowControl == FlowControl.Branch ||
                            nextInstr.OpCode.FlowControl == FlowControl.Cond_Branch ||
                            nextInstr.OpCode.FlowControl == FlowControl.Ret ||
                            nextInstr.OpCode.FlowControl == FlowControl.Switch)
                            break;
                            
                        // If next instruction consumes stack value, arithmetic might be needed
                        if (nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1 ||
                            nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1_pop1 ||
                            nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1_pop1_pop1 ||
                            nextInstr.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                        {
                            // Check if it's a store or another arithmetic op
                            if (nextInstr.IsStloc() || nextInstr.IsStarg() || junkCodes.Contains(nextInstr.OpCode.Code))
                                continue;
                            isUsed = true;
                            break;
                        }
                    }
                    
                    if (!isUsed)
                    {
                        block.Instructions.RemoveAt(i);
                        modified = true;
                    }
                }
            }
            return modified;
        }

        private bool RemoveUselessConversions(Block block)
        {
            bool modified = false;
            var conversionCodes = new HashSet<Code>
            {
                Code.Conv_I4, Code.Conv_U4, Code.Conv_I8, Code.Conv_U8,
                Code.Conv_R4, Code.Conv_R8, Code.Conv_I, Code.Conv_U,
                Code.Conv_Ovf_I4, Code.Conv_Ovf_I4_Un, Code.Conv_Ovf_U4,
                Code.Conv_Ovf_U4_Un, Code.Conv_Ovf_I8, Code.Conv_Ovf_I8_Un,
                Code.Conv_Ovf_U8, Code.Conv_Ovf_U8_Un, Code.Conv_Ovf_I,
                Code.Conv_Ovf_I_Un, Code.Conv_Ovf_U, Code.Conv_Ovf_U_Un
            };

            for (int i = block.Instructions.Count - 2; i >= 0; i--)
            {
                var instr = block.Instructions[i];
                if (conversionCodes.Contains(instr.OpCode.Code))
                {
                    // Check if conversion result is immediately discarded or reconverted
                    bool isUseless = false;
                    if (i + 1 < block.Instructions.Count)
                    {
                        var nextInstr = block.Instructions[i + 1];
                        if (nextInstr.OpCode.Code == Code.Pop ||
                            (conversionCodes.Contains(nextInstr.OpCode.Code) && i + 2 < block.Instructions.Count && 
                             block.Instructions[i + 2].OpCode.Code == Code.Pop))
                        {
                            isUseless = true;
                        }
                    }
                    
                    if (isUseless)
                    {
                        block.Instructions.RemoveAt(i);
                        modified = true;
                    }
                }
            }
            return modified;
        }

        private bool RemoveDupPopPairs(Block block)
        {
            bool modified = false;
            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                if (block.Instructions[i].OpCode.Code == Code.Dup &&
                    block.Instructions[i + 1].OpCode.Code == Code.Pop)
                {
                    block.Instructions.RemoveAt(i + 1);
                    block.Instructions.RemoveAt(i);
                    modified = true;
                    i--; // Adjust index after removal
                }
            }
            return modified;
        }

        private bool RemovePushPopPairs(Block block)
        {
            bool modified = false;
            var pushCodes = new HashSet<Code>
            {
                Code.Ldc_I4, Code.Ldc_I4_S, Code.Ldc_I8, Code.Ldc_R4, Code.Ldc_R8,
                Code.Ldnull, Code.Ldstr, Code.Ldtrue, Code.Ldfalse
            };

            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                if (pushCodes.Contains(block.Instructions[i].OpCode.Code) &&
                    block.Instructions[i + 1].OpCode.Code == Code.Pop)
                {
                    block.Instructions.RemoveAt(i + 1);
                    block.Instructions.RemoveAt(i);
                    modified = true;
                    i--; // Adjust index after removal
                }
            }
            return modified;
        }

        private bool RemoveRedundantLoadStore(Block block)
        {
            bool modified = false;
            
            // Pattern: ldloc.x -> stloc.x (same variable) is useless
            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                var loadInstr = block.Instructions[i];
                var storeInstr = block.Instructions[i + 1];
                
                if (loadInstr.IsLdloc() && storeInstr.IsStloc())
                {
                    var loadLocal = Instr.GetLocalVar(_blocks.Locals, loadInstr);
                    var storeLocal = Instr.GetLocalVar(_blocks.Locals, storeInstr);
                    
                    if (loadLocal == storeLocal)
                    {
                        block.Instructions.RemoveAt(i + 1);
                        block.Instructions.RemoveAt(i);
                        modified = true;
                        i--; // Adjust index
                    }
                }
            }
            return modified;
        }

        private bool RemoveUnreachableCode(Block block)
        {
            bool modified = false;
            
            // Find last branch/ret instruction
            int lastBranchIndex = -1;
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var flow = block.Instructions[i].OpCode.FlowControl;
                if (flow == FlowControl.Branch || flow == FlowControl.Ret || 
                    flow == FlowControl.Throw || flow == FlowControl.Switch)
                {
                    lastBranchIndex = i;
                    break;
                }
            }
            
            // Remove everything after unconditional branch/ret
            if (lastBranchIndex >= 0 && lastBranchIndex < block.Instructions.Count - 1)
            {
                var lastInstr = block.Instructions[lastBranchIndex];
                if (lastInstr.OpCode.FlowControl == FlowControl.Branch ||
                    lastInstr.OpCode.FlowControl == FlowControl.Ret ||
                    lastInstr.OpCode.FlowControl == FlowControl.Throw)
                {
                    block.Instructions.RemoveRange(lastBranchIndex + 1, 
                        block.Instructions.Count - lastBranchIndex - 1);
                    modified = true;
                }
            }
            return modified;
        }

        #endregion
    }
}
