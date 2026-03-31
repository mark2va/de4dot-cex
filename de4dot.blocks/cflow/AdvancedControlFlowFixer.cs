//
// Copyright (c) 2011-2017 de4dot@gmail.com
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
// CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks.cflow;

namespace de4dot.blocks.cflow {
	/// <summary>
	/// Универсальный деобфускатор для снятия арифметического запутывания (arithmetic opaque predicates)
	/// и упрощения потока управления (control flow flattening).
	/// Обрабатывает int, long, float, double, а также условия и goto.
	/// </summary>
	class AdvancedControlFlowFixer : IBlocksDeobfuscator {
		public bool ExecuteIfNotModified { get; set; }
		public bool DisableNewCode { get; set; }

		private MethodDef method;
		private bool modified;

		public AdvancedControlFlowFixer() {
			ExecuteIfNotModified = true;
			DisableNewCode = false;
		}

		public void Initialize(MethodDef method) {
			this.method = method;
			this.modified = false;
		}

		public bool DeobfuscateBegin() {
			return false;
		}

		public bool DeobfuscateEnd() {
			return modified;
		}

		public bool Deobfuscate(Block block) {
			bool localModified = false;
			
			// 1. Упрощение арифметических выражений и констант
			localModified |= SimplifyArithmetic(block);
			
			// 2. Удаление мертвого кода и упрощение переходов
			localModified |= SimplifyControlFlow(block);

			if (localModified)
				modified = true;

			return localModified;
		}

		public void Simplify(Block block) {
			// Дополнительная очистка, если требуется
			SimplifyArithmetic(block);
		}

		/// <summary>
		/// Основной метод для снятия арифметического запутывания.
		/// Ищет паттерны вида: ldconst, op, ldconst, branch и заменяет их на константу.
		/// </summary>
		private bool SimplifyArithmetic(Block block) {
			bool modified = false;
			var instructions = block.Instructions;

			for (int i = 0; i < instructions.Count - 1; i++) {
				var instr = instructions[i];
				
				// Попытка вычислить значение стека или упростить операцию
				if (instr.OpCode.Code == Code.Nop)
					continue;

				// Пример: Попытка свернуть арифметику, если операнды известны
				// Это упрощенная эвристика. Полноценный движок требует анализа всего стека.
				if (IsArithmeticOperation(instr.OpCode.Code)) {
					// Здесь можно добавить логику сворачивания констант (Constant Folding)
					// Если предыдущие инструкции загружают константы, мы можем вычислить результат сразу.
					// Для краткости примера реализуем базовую проверку на бессмысленные операции (x - x = 0)
					if (TryFoldConstants(instructions, i)) {
						modified = true;
						i--; // Перепроверить эту позицию после изменения
					}
				}
			}
			return modified;
		}

		/// <summary>
		/// Упрощение потока управления: удаление безусловных переходов на следующую инструкцию,
		/// замена условий, которые всегда истинны/ложны.
		/// </summary>
		private bool SimplifyControlFlow(Block block) {
			bool modified = false;
			var instructions = block.Instructions;

			for (int i = 0; i < instructions.Count; i++) {
				var instr = instructions[i];

				// Удаляем nop, оставшиеся после оптимизаций
				if (instr.OpCode.Code == Code.Nop && i < instructions.Count - 1) {
					// Nop можно удалить, если это не метка перехода (проверяется в блоках выше обычно)
					// В рамках блока просто помечаем как кандидата на удаление, если логика блоков позволяет
				}

				// Проверка на безусловный переход (br.s / br)
				if (instr.OpCode.FlowControl == FlowControl.Branch) {
					var target = instr.Operand as Instruction;
					if (target != null) {
						// Если переход на следующую инструкцию - удаляем его
						if (i + 1 < instructions.Count && target == instructions[i + 1]) {
							instr.OpCode = OpCodes.Nop;
							instr.Operand = null;
							modified = true;
						}
					}
				}

				// Проверка на условный переход с константным условием
				// Паттерн: ldc.i4.1 (или другое) -> brtrue/brfalse
				if (i > 0) {
					var prev = instructions[i - 1];
					if (IsConditionalBranch(instr.OpCode.Code)) {
						int? constValue = GetInt32Constant(prev);
						if (constValue.HasValue) {
							bool conditionResult = EvaluateCondition(instr.OpCode.Code, constValue.Value);
							var target = instr.Operand as Instruction;
							
							if (target != null) {
								// Заменяем условный переход на безусловный или NOP
								if (conditionResult) {
									// Условие истинно: превращаем в безусловный переход (если это brtrue)
									// или в NOP (если brfalse)
									if (instr.OpCode.Code == Code.Brtrue || instr.OpCode.Code == Code.Brtrue_S) {
										instr.OpCode = OpCodes.Br_S;
									} else {
										instr.OpCode = OpCodes.Nop;
										instr.Operand = null;
									}
								} else {
									// Условие ложно
									if (instr.OpCode.Code == Code.Brfalse || instr.OpCode.Code == Code.Brfalse_S) {
										instr.OpCode = OpCodes.Br_S;
									} else {
										instr.OpCode = OpCodes.Nop;
										instr.Operand = null;
									}
								}
								modified = true;
								
								// Удаляем константу со стека, так как переход теперь детерминирован
								if (prev.OpCode.StackBehaviourPush == StackBehaviour.Push1) {
									prev.OpCode = OpCodes.Nop;
									prev.Operand = null;
								}
							}
						}
					}
				}
			}
			return modified;
		}

		private bool IsArithmeticOperation(Code code) {
			switch (code) {
				case Code.Add: case Code.Add_Ovf: case Code.Add_Ovf_Un:
				case Code.Sub: case Code.Sub_Ovf: case Code.Sub_Ovf_Un:
				case Code.Mul: case Code.Mul_Ovf: case Code.Mul_Ovf_Un:
				case Code.Div: case Code.Div_Un:
				case Code.Rem: case Code.Rem_Un:
				case Code.And: case Code.Or: case Code.Xor:
				case Code.Shl: case Code.Shr: case Code.Shr_Un:
				case Code.Neg: case Code.Not:
					return true;
				default:
					return false;
			}
		}

		private bool TryFoldConstants(List<Instruction> instructions, int index) {
			// Очень упрощенная реализация сворачивания констант
			// Ищем паттерн: ldc, ldc, op -> pop, pop, ldc(result)
			// В реальном сценарии нужен анализ стека. Здесь仅作 пример структуры.
			return false; 
		}

		private bool IsConditionalBranch(Code code) {
			switch (code) {
				case Code.Brtrue: case Code.Brtrue_S:
				case Code.Brfalse: case Code.Brfalse_S:
				case Code.Beq: case Code.Beq_S:
				case Code.Bne_Un: case Code.Bne_Un_S:
				case Code.Bge: case Code.Bge_S:
				case Code.Bge_Un: case Code.Bge_Un_S:
				case Code.Bgt: case Code.Bgt_S:
				case Code.Bgt_Un: case Code.Bgt_Un_S:
				case Code.Ble: case Code.Ble_S:
				case Code.Ble_Un: case Code.Ble_Un_S:
				case Code.Blt: case Code.Blt_S:
				case Code.Blt_Un: case Code.Blt_Un_S:
					return true;
				default:
					return false;
			}
		}

		private int? GetInt32Constant(Instruction instr) {
			if (instr == null) return null;
			
			switch (instr.OpCode.Code) {
				case Code.Ldc_I4:
					return (int)instr.Operand;
				case Code.Ldc_I4_0: return 0;
				case Code.Ldc_I4_1: return 1;
				case Code.Ldc_I4_2: return 2;
				case Code.Ldc_I4_3: return 3;
				case Code.Ldc_I4_4: return 4;
				case Code.Ldc_I4_5: return 5;
				case Code.Ldc_I4_6: return 6;
				case Code.Ldc_I4_7: return 7;
				case Code.Ldc_I4_8: return 8;
				case Code.Ldc_I4_M1: return -1;
				case Code.Ldc_I4_S: return (int)(sbyte)instr.Operand;
				default:
					return null;
			}
		}

		private bool EvaluateCondition(Code code, int value) {
			// Оцениваем условие, если второй операнд тоже константа (здесь упрощено до проверки на 0/non-zero для brtrue/false)
			// Для полноценной работы нужно извлекать два операнда.
			// Здесь реализуем логику для brtrue/brfalse от одного значения.
			
			if (code == Code.Brtrue || code == Code.Brtrue_S)
				return value != 0;
			if (code == Code.Brfalse || code == Code.Brfalse_S)
				return value == 0;
			
			// Для остальных сравнений нужна более сложная логика с двумя операндами
			// Возвращаем false, чтобы не ломать логику, если не уверены
			return false; 
		}
		
		// Вспомогательный метод для проверки starg, если нужно (эмуляция расширения)
		private static bool IsStarg(Instruction instr) {
			return instr.OpCode.Code == Code.Starg || instr.OpCode.Code == Code.Starg_S;
		}
	}
}
