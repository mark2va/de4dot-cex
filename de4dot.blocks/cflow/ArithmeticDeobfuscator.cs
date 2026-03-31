/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.blocks.cflow {
	/// <summary>
	/// Деобфускатор для снятия арифметического запутывания
	/// Обрабатывает паттерны где переменным присваиваются значения int, float, long
	/// и далее идёт while/do цикл с кучей условий if и goto
	/// </summary>
	class ArithmeticDeobfuscator : BlockDeobfuscator {
		InstructionEmulator instructionEmulator = new InstructionEmulator();
		IList<Parameter> args;

		public bool DisableNewCode { get; set; }

		protected override void Initialize(List<Block> allBlocks) {
			base.Initialize(allBlocks);
			args = blocks.Method.Parameters;
		}

		protected override bool Deobfuscate(Block block) {
			bool modified = false;

			instructionEmulator.Initialize(blocks, allBlocks[0] == block);
			var instrs = block.Instructions;
			
			// Проходим по инструкциям и пытаемся упростить арифметические выражения
			for (int i = 0; i < instrs.Count; i++) {
				var instr = instrs[i];

				switch (instr.OpCode.Code) {
				case Code.Ldarg:
				case Code.Ldarg_0:
				case Code.Ldarg_1:
				case Code.Ldarg_2:
				case Code.Ldarg_3:
				case Code.Ldarg_S:
					modified |= FixLoadInstruction(block, i, instructionEmulator.GetArg(instr.Instruction.GetParameter(args)));
					break;

				case Code.Ldloc:
				case Code.Ldloc_0:
				case Code.Ldloc_1:
				case Code.Ldloc_2:
				case Code.Ldloc_3:
				case Code.Ldloc_S:
					modified |= FixLoadInstruction(block, i, instructionEmulator.GetLocal(instr.Instruction.GetLocal(blocks.Locals)));
					break;

				case Code.Ldarga:
				case Code.Ldarga_S:
					instructionEmulator.MakeArgUnknown((Parameter)instr.Operand);
					break;

				case Code.Ldloca:
				case Code.Ldloca_S:
					instructionEmulator.MakeLocalUnknown((Local)instr.Operand);
					break;

				// Арифметические операции
				case Code.Add:
				case Code.Add_Ovf:
				case Code.Add_Ovf_Un:
				case Code.And:
				case Code.Ceq:
				case Code.Cgt:
				case Code.Cgt_Un:
				case Code.Clt:
				case Code.Clt_Un:
				case Code.Conv_I:
				case Code.Conv_I1:
				case Code.Conv_I2:
				case Code.Conv_I4:
				case Code.Conv_I8:
				case Code.Conv_Ovf_I:
				case Code.Conv_Ovf_I_Un:
				case Code.Conv_Ovf_I1:
				case Code.Conv_Ovf_I1_Un:
				case Code.Conv_Ovf_I2:
				case Code.Conv_Ovf_I2_Un:
				case Code.Conv_Ovf_I4:
				case Code.Conv_Ovf_I4_Un:
				case Code.Conv_Ovf_I8:
				case Code.Conv_Ovf_I8_Un:
				case Code.Conv_Ovf_U:
				case Code.Conv_Ovf_U_Un:
				case Code.Conv_Ovf_U1:
				case Code.Conv_Ovf_U1_Un:
				case Code.Conv_Ovf_U2:
				case Code.Conv_Ovf_U2_Un:
				case Code.Conv_Ovf_U4:
				case Code.Conv_Ovf_U4_Un:
				case Code.Conv_Ovf_U8:
				case Code.Conv_Ovf_U8_Un:
				case Code.Conv_R_Un:
				case Code.Conv_R4:
				case Code.Conv_R8:
				case Code.Conv_U:
				case Code.Conv_U1:
				case Code.Conv_U2:
				case Code.Conv_U4:
				case Code.Conv_U8:
				case Code.Div:
				case Code.Div_Un:
				case Code.Dup:
				case Code.Mul:
				case Code.Mul_Ovf:
				case Code.Mul_Ovf_Un:
				case Code.Neg:
				case Code.Not:
				case Code.Or:
				case Code.Rem:
				case Code.Rem_Un:
				case Code.Shl:
				case Code.Shr:
				case Code.Shr_Un:
				case Code.Sub:
				case Code.Sub_Ovf:
				case Code.Sub_Ovf_Un:
				case Code.Xor:
					if (DisableNewCode)
						break;
					// Пропускаем если результат сразу удаляется
					if (i + 1 < instrs.Count && instrs[i + 1].OpCode.Code == Code.Pop)
						break;
					if (!VerifyValidArgs(instr.Instruction))
						break;
					instructionEmulator.Emulate(instr.Instruction);
					var tos = instructionEmulator.Peek();
					Instruction newInstr = null;
					if (tos.IsInt32()) {
						var val = (Int32Value)tos;
						if (val.AllBitsValid())
							newInstr = Instruction.CreateLdcI4(val.Value);
					}
					else if (tos.IsInt64()) {
						var val = (Int64Value)tos;
						if (val.AllBitsValid())
							newInstr = OpCodes.Ldc_I8.ToInstruction(val.Value);
					}
					else if (tos.IsReal8()) {
						var val = (Real8Value)tos;
						if (val.IsValid)
							newInstr = GetLoadRealInstruction(val.Value);
					}
					if (newInstr != null) {
						block.Insert(i + 1, Instruction.Create(OpCodes.Pop));
						block.Insert(i + 2, newInstr);
						i += 2;
						modified = true;
					}
					continue;
				}

				try {
					instructionEmulator.Emulate(instr.Instruction);
				}
				catch (NullReferenceException) {
					// Here if eg. invalid metadata token in a call instruction (operand is null)
					break;
				}
			}

			// Дополнительная обработка для удаления мертвого кода
			modified |= RemoveDeadArithmetic(block);
			modified |= SimplifyComparisons(block);

			return modified;
		}

		/// <summary>
		/// Удаляет мертвые арифметические операции
		/// </summary>
		private bool RemoveDeadArithmetic(Block block) {
			bool modified = false;
			var junkCodes = new HashSet<Code> {
				Code.Add, Code.Sub, Code.Mul, Code.Div, Code.Rem,
				Code.And, Code.Or, Code.Xor, Code.Shl, Code.Shr, Code.Shr_Un,
				Code.Neg, Code.Not
			};

			for (int i = block.Instructions.Count - 2; i >= 0; i--) {
				var instr = block.Instructions[i];
				if (junkCodes.Contains(instr.OpCode.Code)) {
					// Проверяем используется ли результат или просто отбрасывается
					bool isUsed = false;
					for (int j = i + 1; j < block.Instructions.Count; j++) {
						var nextInstr = block.Instructions[j];
						if (nextInstr.OpCode.FlowControl == FlowControl.Branch ||
							nextInstr.OpCode.FlowControl == FlowControl.Cond_Branch ||
							nextInstr.OpCode.FlowControl == FlowControl.Ret ||
							nextInstr.OpCode.FlowControl == FlowControl.Switch)
							break;
							
						// Если следующая инструкция потребляет значение со стека
						if (nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1 ||
							nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1_pop1 ||
							nextInstr.OpCode.StackBehaviourPop == StackBehaviour.Pop1_pop1_pop1 ||
							nextInstr.OpCode.StackBehaviourPop == StackBehaviour.PopAll) {
							// Проверяем это store или другая арифметическая операция
							if (nextInstr.IsStloc() || nextInstr.IsStarg() || junkCodes.Contains(nextInstr.OpCode.Code))
								continue;
							isUsed = true;
							break;
						}
					}
					
					if (!isUsed) {
						block.Instructions.RemoveAt(i);
						modified = true;
					}
				}
			}
			return modified;
		}

		/// <summary>
		/// Упрощает сравнения с константами
		/// </summary>
		private bool SimplifyComparisons(Block block) {
			bool modified = false;
			
			for (int i = 0; i < block.Instructions.Count - 1; i++) {
				var instr = block.Instructions[i];
				
				// Ищем ldc.i4 / ldc.i8 перед сравнением
				if (!instr.IsLdcI4() && instr.OpCode.Code != Code.Ldc_I8)
					continue;
					
				var nextInstr = block.Instructions[i + 1];
				Code cmpCode = nextInstr.OpCode.Code;
				
				// Проверяем является ли следующая инструкция сравнением
				if (cmpCode != Code.Ceq && cmpCode != Code.Cgt && cmpCode != Code.Cgt_Un && 
					cmpCode != Code.Clt && cmpCode != Code.Clt_Un)
					continue;
				
				// Пытаемся эмулировать и упростить
				instructionEmulator.Emulate(instr.Instruction);
				instructionEmulator.Emulate(nextInstr.Instruction);
				
				var result = instructionEmulator.Pop();
				if (result.IsInt32()) {
					var val = (Int32Value)result;
					if (val.AllBitsValid()) {
						// Заменяем сравнение на константу
						block.Instructions.RemoveAt(i + 1);
						block.Instructions[i] = new Instr(Instruction.CreateLdcI4(val.Value));
						modified = true;
					}
				}
			}
			
			return modified;
		}

		bool VerifyValidArgs(Instruction instr) {
			int pushes, pops;
			instr.CalculateStackUsage(out pushes, out pops);
			if (pops < 0)
				return false;

			bool retVal;
			Value val2, val1;
			switch (pops) {
			case 0:
				return true;

			case 1:
				val1 = instructionEmulator.Pop();
				retVal = VerifyValidArg(val1);
				instructionEmulator.Push(val1);
				return retVal;

			case 2:
				val2 = instructionEmulator.Pop();
				val1 = instructionEmulator.Pop();
				retVal = VerifyValidArg(val2) && VerifyValidArg(val1);
				instructionEmulator.Push(val1);
				instructionEmulator.Push(val2);
				return retVal;
			}

			return false;
		}

		static bool VerifyValidArg(Value value) {
			if (value.IsInt32())
				return ((Int32Value)value).AllBitsValid();
			if (value.IsInt64())
				return ((Int64Value)value).AllBitsValid();
			if (value.IsReal8())
				return ((Real8Value)value).IsValid;
			return false;
		}

		static Instruction GetLoadRealInstruction(double value) {
			var floatVal = (float)value;
			if (floatVal == value || double.IsNaN(value))
				return OpCodes.Ldc_R4.ToInstruction(floatVal);
			return OpCodes.Ldc_R8.ToInstruction(value);
		}

		bool FixLoadInstruction(Block block, int index, Value value) {
			if (value.IsInt32()) {
				var intValue = (Int32Value)value;
				if (!intValue.AllBitsValid())
					return false;
				block.Instructions[index] = new Instr(Instruction.CreateLdcI4(intValue.Value));
				return true;
			}
			else if (value.IsInt64()) {
				var intValue = (Int64Value)value;
				if (!intValue.AllBitsValid())
					return false;
				block.Instructions[index] = new Instr(OpCodes.Ldc_I8.ToInstruction(intValue.Value));
				return true;
			}
			else if (value.IsReal8()) {
				var realValue = (Real8Value)value;
				if (!realValue.IsValid)
					return false;
				block.Instructions[index] = new Instr(GetLoadRealInstruction(realValue.Value));
				return true;
			}
			return false;
		}
	}
}
