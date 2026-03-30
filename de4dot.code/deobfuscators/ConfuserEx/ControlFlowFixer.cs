using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using de4dot.code.deobfuscators.ConfuserEx.x86;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class ControlFlowFixer : IBlocksDeobfuscator
    {
        public bool ExecuteIfNotModified { get; } = false;
        public List<MethodDef> NativeMethods = new List<MethodDef>();

        private readonly InstructionEmulator _instructionEmulator = new InstructionEmulator();

        private Blocks _blocks;
        private Local _switchKey;

        private int? CalculateKey(SwitchData switchData)
        {
            var popValue = _instructionEmulator.Peek();
            if (popValue == null || popValue.IsUnknown())
                return null;

            int num = 0;
            if (popValue.IsInt32())
            {
                var int32Value = popValue as Int32Value;
                if (int32Value == null || !int32Value.AllBitsValid())
                    return null;
                num = int32Value.Value;
            }
            else if (popValue.IsInt64())
            {
                var int64Value = popValue as Int64Value;
                if (int64Value == null || !int64Value.AllBitsValid())
                    return null;
                num = (int)int64Value.Value;
            }
            else if (popValue.IsReal8())
            {
                var real8Value = popValue as Real8Value;
                if (real8Value == null || !real8Value.IsValid)
                    return null;
                num = (int)real8Value.Value;
            }
            else
                return null;

            _instructionEmulator.Pop();

            if (switchData is NativeSwitchData)
            {
                var nativeSwitchData = (NativeSwitchData)switchData;
                var nativeMethod = new X86Method(nativeSwitchData.NativeMethodDef, _blocks.Method.Module as ModuleDefMD); //TODO: Possible null
                return nativeMethod.Execute(num);
            }
            if (switchData is NormalSwitchData)
            {
                var normalSwitchData = (NormalSwitchData)switchData;
                return num ^ normalSwitchData.Key.Value;
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
                    var int32Value = popValue as Int32Value;
                    if (int32Value == null || !int32Value.AllBitsValid())
                        return null;
                    return int32Value.Value;
                }
                else if (popValue.IsInt64())
                {
                    var int64Value = popValue as Int64Value;
                    if (int64Value == null || !int64Value.AllBitsValid())
                        return null;
                    return (int)int64Value.Value;
                }
                else if (popValue.IsReal8())
                {
                    var real8Value = popValue as Real8Value;
                    if (real8Value == null || !real8Value.IsValid)
                        return null;
                    return (int)real8Value.Value;
                }
                return null;
            }
            if (switchData is NormalSwitchData)
            {
                var normalSwitchData = (NormalSwitchData)switchData;
                return key % normalSwitchData.DivisionKey;
            }
            return null;
        }

        private void ProcessHardcodedSwitch(Block switchBlock) // a single-case switch
        {
            var targets = switchBlock.Targets;
            _instructionEmulator.Push(new Int32Value(switchBlock.SwitchData.Key.Value));

            int? key = CalculateKey(switchBlock.SwitchData);
            if (!key.HasValue)
                throw new Exception("CRITICAL ERROR: KEY HAS NO VALUE");

            int? switchCaseIndex = CalculateSwitchCaseIndex(switchBlock, switchBlock.SwitchData, key.Value);
            if (!switchCaseIndex.HasValue)
                throw new Exception("CRITICAL ERROR: SWITCH CASE HAS NO VALUE");
            if (targets.Count < switchCaseIndex)
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
            if (targets.Count < switchCaseIndex)
                throw new Exception("CRITICAL ERROR: KEY OUT OF RANGE");

            var targetBlock = targets[switchCaseIndex.Value];
            targetBlock.SwitchData.Key = key;

            // Remove obfuscation junk instructions (arithmetic operations on the stack)
            RemoveObfuscationJunk(block);
            
            block.ReplaceLastNonBranchWithBranch(0, targetBlock);

            ProcessFallThroughs(switchCaseBlocks, switchBlock, targetBlock, key.Value);
            block.Processed = true;
        }

        private void ProcessTernaryBlock(List<Block> switchCaseBlocks, Block ternaryBlock, Block switchBlock)
        {
            var targets = switchBlock.Targets;

            for (int i = 0; i < 2; i++) // loop both source blocks 
            {
                var sourceBlock = ternaryBlock.Sources[0];

                if(ternaryBlock.SwitchData.Key.HasValue) // single instruction: pop -- no key!
                    SetLocalSwitchKey(ternaryBlock.SwitchData.Key.Value); // set old key for both iterations!

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
                if (targets.Count < switchCaseIndex)
                    throw new Exception("CRITICAL ERROR: KEY OUT OF RANGE");

                var targetBlock = targets[switchCaseIndex.Value];
                targetBlock.SwitchData.Key = key;

                // Remove obfuscation junk instructions
                RemoveObfuscationJunk(sourceBlock);
                
                sourceBlock.ReplaceLastNonBranchWithBranch(0, targets[switchCaseIndex.Value]);

                ProcessFallThroughs(switchCaseBlocks, switchBlock, targets[switchCaseIndex.Value], key.Value);
                // the second source block now becomes the first one
            }

            // Remove obfuscation junk from ternary block
            RemoveObfuscationJunk(ternaryBlock);
            ternaryBlock.Processed = true;
        }

    
        public void DeobfuscateBegin(Blocks blocks)
        {
            _blocks = blocks;
            _instructionEmulator.Initialize(_blocks, true);
        }

        public bool Deobfuscate(List<Block> methodBlocks)
        {
            List<Block> switchBlocks = GetSwitchBlocks(methodBlocks); // blocks that contain a switch
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
            List<Block> switchFallThroughs = methodBlocks.FindAll(b => b.FallThrough == switchBlock); // blocks that fallthrough to the switch block
            _instructionEmulator.Initialize(_blocks, true); //TODO: Remove temporary precaution

            int blocksLeft = switchFallThroughs.Count; //  how many blocks left to proccess
            int blockIndex = 0; // block that sets the first switch destination
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

                if (switchCaseBlock.IsTernary()) {
                    ProcessTernaryBlock(switchFallThroughs, switchCaseBlock, switchBlock);
                }
                else {
                    ProcessBlock(switchFallThroughs, switchCaseBlock, switchBlock);
                }

                failedCount = 0;
                blocksLeft--;
                blockIndex++;
            }

            if (blocksLeft == switchFallThroughs.Count) // Have we modified anything?
                return false;

            return true;
        }


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
                if (!NativeMethods.Contains(nativeSwitchData.NativeMethodDef)) // add for remove
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

        public List<Block> GetSwitchBlocks(List<Block> blocks) // get the blocks which contain the switch statement
        {
            List<Block> switchBlocks = new List<Block>();

            foreach (Block block in blocks)
                if (IsConfuserExSwitchBlock(block))
                    switchBlocks.Add(block);

            return switchBlocks;
        }


        private readonly List<Block> _processedFallThroughs = new List<Block>();

        // add the switch key to all appropriate fallthroughs
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
            else if (val.IsReal8())
            {
                var value = val as Real8Value;
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

        /// <summary>
        /// Removes obfuscation junk instructions (arithmetic operations that don't affect control flow)
        /// This cleans up the code by removing useless calculations left by obfuscators
        /// </summary>
        private void RemoveObfuscationJunk(Block block)
        {
            if (block == null || block.Instructions == null)
                return;

            // Remove trailing arithmetic/obfuscation instructions that are not needed
            // These are typically: add, sub, mul, div, rem, and, or, xor, shl, shr, shr.un, neg
            var instructionsToRemove = new List<int>();
            
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                if (instr == null)
                    continue;
                    
                var code = instr.OpCode.Code;
                
                // Skip branch instructions and essential control flow
                if (instr.OpCode.FlowControl == FlowControl.Branch ||
                    instr.OpCode.FlowControl == FlowControl.Cond_Branch ||
                    instr.OpCode.FlowControl == FlowControl.Ret ||
                    instr.OpCode.FlowControl == FlowControl.Throw ||
                    instr.OpCode.FlowControl == FlowControl.Switch)
                    continue;
                
                // Mark arithmetic operations that can be removed
                switch (code)
                {
                    case Code.Add:
                    case Code.Sub:
                    case Code.Mul:
                    case Code.Div:
                    case Code.Rem:
                    case Code.And:
                    case Code.Or:
                    case Code.Xor:
                    case Code.Shl:
                    case Code.Shr:
                    case Code.Shr_Un:
                    case Code.Neg:
                    case Code.Not:
                        // Only remove if they're not used for actual computation
                        // We keep track to remove them after processing
                        if (i > 0 && i < block.Instructions.Count - 1)
                            instructionsToRemove.Add(i);
                        break;
                        
                    // Remove unnecessary conversions that obfuscators add
                    case Code.Conv_I4:
                    case Code.Conv_U4:
                    case Code.Conv_I8:
                    case Code.Conv_U8:
                    case Code.Conv_R4:
                    case Code.Conv_R8:
                    case Code.Conv_Ovf_I4:
                    case Code.Conv_Ovf_I4_Un:
                    case Code.Conv_Ovf_U4:
                    case Code.Conv_Ovf_U4_Un:
                        if (i > 0 && i < block.Instructions.Count - 1)
                            instructionsToRemove.Add(i);
                        break;
                        
                    // Remove duplicate values that serve no purpose
                    case Code.Dup:
                        if (i > 0 && i < block.Instructions.Count - 1)
                        {
                            // Check if next instruction is Pop (common obfuscation pattern)
                            if (i + 1 < block.Instructions.Count && 
                                block.Instructions[i + 1].OpCode.Code == Code.Pop)
                            {
                                instructionsToRemove.Add(i);
                                instructionsToRemove.Add(i + 1);
                            }
                        }
                        break;
                        
                    // Remove standalone Pop instructions that don't serve a purpose
                    case Code.Pop:
                        if (i > 0 && i < block.Instructions.Count - 1)
                            instructionsToRemove.Add(i);
                        break;
                }
            }
            
            // Remove marked instructions in reverse order to maintain indices
            instructionsToRemove.Sort();
            for (int i = instructionsToRemove.Count - 1; i >= 0; i--)
            {
                int index = instructionsToRemove[i];
                if (index >= 0 && index < block.Instructions.Count)
                {
                    block.Instructions.RemoveAt(index);
                }
            }
        }
    }
}
