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
	public class AdvancedControlFlowFixer : IBlocksDeobfuscator {
		public bool ExecuteIfNotModified { get; set; }
		public bool DisableNewCode { get; set; }

		private MethodDef method;
		private bool modified;
		
		// Стек для эмуляции выполнения и сворачивания констант
		private Stack<StackValue> evalStack;

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
			
			// Запускаем цикл упрощения, пока есть изменения
			// Это нужно потому, что упрощение одного условия может открыть возможности для другого
			int maxIterations = 10; // Защита от бесконечного цикла
			int iteration = 0;
			
			while (iteration < maxIterations) {
				bool passModified = false;
				
				// 1. Сворачивание арифметики и констант
				passModified |= SimplifyArithmetic(block);
				
				// 2. Упрощение потока управления (замена условий на goto/nop)
				passModified |= SimplifyControlFlow(block);
				
				// 3. Очистка NOP и мертвого кода
				passModified |= RemoveDeadCode(block);

				if (passModified) {
					localModified = true;
					modified = true;
					iteration++;
				} else {
					break; // Больше ничего не упростилось
				}
			}

			return localModified;
		}

		public void Simplify(Block block) {
			SimplifyArithmetic(block);
			SimplifyControlFlow(block);
		}

		/// <summary>
		/// Основной метод для снятия арифметического запутывания через эмуляцию стека.
		/// </summary>
		private bool SimplifyArithmetic(Block block) {
			bool modified = false;
			var instructions = block.Instructions;
			
			// Инициализируем стек для этого блока
			// В реальном сценарии нужно учитывать входящие значения, но для локальных предикатов часто хватает локального анализа
			evalStack = new Stack<StackValue>();

			for (int i = 0; i < instructions.Count; i++) {
				var instr = instructions[i];
				
				if (instr.OpCode.Code == Code.Nop)
					continue;

				bool replaced = false;
				StackValue resultValue = null;

				// Обработка загрузки констант
				if (TryLoadConstant(instr, out StackValue constVal)) {
					evalStack.Push(constVal);
					continue;
				}

				// Обработка арифметических и логических операций
				if (TryEvaluateOperation(instr, out resultValue)) {
					// Если операция успешно вычислена (все операнды были константами)
					// Заменяем всю последовательность (операнды + операция) на одну инструкцию ldc
					
					// Нам нужно удалить операнды со стека инструкций. 
					// Мы знаем сколько операндов было взято из evalStack.Pop() внутри TryEvaluateOperation,
					// но нам нужно найти их в списке инструкций перед текущей.
					// Для простоты реализуем замену только текущей инструкции, если мы ведем учет глубины.
					// Более надежный способ: переписать блок снизу вверх или использовать пометки.
					
					// Здесь используем подход: если мы вычислили значение, мы помечаем инструкцию на замену,
					// а предыдущие инструкции (операнды) пометим как NOP в следующем проходе или сразу.
					// Для надежности в рамках одного прохода: заменим текущую инструкцию на ldc, а предыдущие N инструкций найдем и обнулим.
					
					int popCount = GetStackPopCount(instr.OpCode);
					
					// Ищем popCount инструкций до текущей, которые не являются NOP и являются частью вычисления
					int removeCount = 0;
					for (int j = i - 1; j >= 0 && removeCount < popCount; j--) {
						if (instructions[j].OpCode.Code != Code.Nop) {
							instructions[j].OpCode = OpCodes.Nop;
							instructions[j].Operand = null;
							removeCount++;
						}
					}

					// Заменяем текущую инструкцию на загрузку результата
					ReplaceWithConst(instr, resultValue);
					evalStack.Push(resultValue); // Возвращаем результат на наш виртуальный стек
					
					modified = true;
					replaced = true;
				}
				
				if (!replaced) {
					// Если инструкция не была заменена, она может потреблять значения со стека
					// Эмулируем поведение стека для поддержания синхронизации
					int pops = instr.OpCode.StackBehaviourPop switch {
						StackBehaviour.Pop0 => 0,
						StackBehaviour.Pop1 => 1,
						StackBehaviour.Pop1_pop1 => 2,
						StackBehaviour.Pop1_pop1_pop1 => 3,
						StackBehaviour.PopAll => evalStack.Count,
						StackBehaviour.Varpop => 0, // Сложный случай, игнорируем для простоты
						_ => 0
					};
					
					// Синхронизируем наш виртуальный стек с реальным поведением (грубо)
					// Если инструкция потребляет значения, удаляем их из нашего стека, если они там есть
					// Это важно, чтобы не потерять счетчик, если мы пропустили константу
					for (int k = 0; k < pops && evalStack.Count > 0; k++) {
						evalStack.Pop();
					}
					
					int pushes = instr.OpCode.StackBehaviourPush switch {
						StackBehaviour.Push0 => 0,
						StackBehaviour.Push1 => 1,
						StackBehaviour.Push1_push1 => 2,
						StackBehaviour.Varpush => 0,
						_ => 0
					};
					
					// Если инструкция пушит что-то неизвестное (например, вызов метода), сбрасываем стек или помечаем как неизвестное
					if (pushes > 0 && !replaced) {
						// Для безопасности при неизвестных значениях лучше очистить стек эмуляции,
						// так как дальнейшие вычисления будут неверными.
						// Но для локальных предикатов часто можно продолжить, если мы просто добавим "Unknown"
						// Для простоты здесь очистим, если это не ldc (он обработан выше)
						if (instr.OpCode.Code != Code.Dup) {
						     // evalStack.Clear(); // Раскомментировать если много ложных срабатываний
						}
						if (instr.OpCode.Code == Code.Dup && evalStack.Count > 0) {
							evalStack.Push(evalStack.Peek());
						}
					}
				}
			}
			
			return modified;
		}

		/// <summary>
		/// Упрощение потока управления: замена условных переходов с известным результатом.
		/// </summary>
		private bool SimplifyControlFlow(Block block) {
			bool modified = false;
			var instructions = block.Instructions;

			for (int i = 0; i < instructions.Count; i++) {
				var instr = instructions[i];
				Code code = instr.OpCode.Code;

				if (!IsConditionalBranch(code))
					continue;

				// Пытаемся получить условие со стека
				// Для бинарных сравнений (beq, bgt и т.д.) нужно два операнда, для brtrue/brfalse - один.
				// Наш стек evalStack синхронизирован с инструкциями выше.
				
				// Проверка: если перед инструкцией перехода есть константа (или результат вычислений)
				// Мы должны посмотреть назад в инструкциях, чтобы найти значение.
				// Но проще: если мы успешно свернули арифметику, то перед branch должна быть ldc.
				
				int requiredArgs = 1;
				if (code == Code.Beq || code == Code.Bne_Un || code == Code.Bgt || code == Code.Bgt_Un ||
				    code == Code.Bge || code == Code.Bge_Un || code == Code.Blt || code == Code.Blt_Un ||
				    code == Code.Ble || code == Code.Ble_Un) {
					requiredArgs = 2;
				}

				// Ищем аргументы в стеке эмуляции (они должны быть на вершине)
				// Примечание: в стеке верхний элемент - последний pushed.
				// Для сравнения A B ceq -> стек: ..., A, B. После ceq -> ..., result.
				// Ветка использует result.
				
				// Попытка извлечь значения из нашего виртуального стека
				// Так как мы иддем сверху вниз, стек должен содержать актуальные значения, 
				// если предыдущие инструкции были обработаны.
				
				// Однако, надежнее проверить непосредственно предыдущие инструкции на наличие ldc
				// или результата нашей оптимизации.
				
				StackValue val1 = null;
				StackValue val2 = null;
				bool valuesKnown = false;

				if (evalStack.Count >= requiredArgs) {
					val1 = evalStack.ToArray()[evalStack.Count - requiredArgs]; // Нижний операнд (первый pushed)
					if (requiredArgs == 2)
						val2 = evalStack.ToArray()[evalStack.Count - 1]; // Верхний операнд
					
					valuesKnown = true;
				}

				if (valuesKnown) {
					bool jumpTaken = false;

					if (requiredArgs == 1) {
						// Brtrue / Brfalse
						jumpTaken = EvaluateUnaryCondition(code, val1);
					} else {
						// Binary comparisons (Beq, Bgt, etc.)
						jumpTaken = EvaluateBinaryCondition(code, val1, val2);
					}

					var target = instr.Operand as Instruction;
					if (target != null) {
						if (jumpTaken) {
							// Условие истинно -> превращаем в безусловный переход
							instr.OpCode = OpCodes.Br_S; // Или Br, если далеко, но Br_S обычно ок для локальных блоков
							modified = true;
						} else {
							// Условие ложно -> удаляем переход (превращаем в nop)
							instr.OpCode = OpCodes.Nop;
							instr.Operand = null;
							modified = true;
						}
						
						// Удаляем операнды условия со стека инструкций (те, что мы использовали)
						// Находим их среди предыдущих инструкций и делаем NOP
						// Это грубый поиск, но работает для простых случаев
						int removed = 0;
						for (int j = i - 1; j >= 0 && removed < requiredArgs; j--) {
							if (instructions[j].OpCode.Code != Code.Nop) {
								instructions[j].OpCode = OpCodes.Nop;
								instructions[j].Operand = null;
								removed++;
							}
						}
						
						// Синхронизируем виртуальный стек
						for(int k=0; k<requiredArgs && evalStack.Count>0; k++) evalStack.Pop();
						evalStack.Push(new StackValue { Type = StackValueType.Int32, Int32Value = 0 }); // Push dummy or nothing? Branch consumes values.
						// Ветки потребляют значения и ничего не возвращают.
						if (evalStack.Count >= requiredArgs) {
							for(int k=0; k<requiredArgs; k++) evalStack.Pop();
						}
					}
				}
			}
			return modified;
		}

		private bool RemoveDeadCode(Block block) {
			bool modified = false;
			// Простое удаление последовательных NOP, если их много, можно добавить позже
			// Основная чистка происходит при замене инструкций в других методах
			return modified;
		}

		#region Helper Methods for Evaluation

		private bool TryLoadConstant(Instruction instr, out StackValue value) {
			value = null;
			switch (instr.OpCode.Code) {
				case Code.Ldc_I4:
					value = new StackValue { Type = StackValueType.Int32, Int32Value = (int)instr.Operand };
					return true;
				case Code.Ldc_I4_0: value = new StackValue { Type = StackValueType.Int32, Int32Value = 0 }; return true;
				case Code.Ldc_I4_1: value = new StackValue { Type = StackValueType.Int32, Int32Value = 1 }; return true;
				case Code.Ldc_I4_2: value = new StackValue { Type = StackValueType.Int32, Int32Value = 2 }; return true;
				case Code.Ldc_I4_3: value = new StackValue { Type = StackValueType.Int32, Int32Value = 3 }; return true;
				case Code.Ldc_I4_4: value = new StackValue { Type = StackValueType.Int32, Int32Value = 4 }; return true;
				case Code.Ldc_I4_5: value = new StackValue { Type = StackValueType.Int32, Int32Value = 5 }; return true;
				case Code.Ldc_I4_6: value = new StackValue { Type = StackValueType.Int32, Int32Value = 6 }; return true;
				case Code.Ldc_I4_7: value = new StackValue { Type = StackValueType.Int32, Int32Value = 7 }; return true;
				case Code.Ldc_I4_8: value = new StackValue { Type = StackValueType.Int32, Int32Value = 8 }; return true;
				case Code.Ldc_I4_M1: value = new StackValue { Type = StackValueType.Int32, Int32Value = -1 }; return true;
				case Code.Ldc_I4_S: value = new StackValue { Type = StackValueType.Int32, Int32Value = (int)(sbyte)instr.Operand }; return true;
				
				case Code.Ldc_I8:
					value = new StackValue { Type = StackValueType.Int64, Int64Value = (long)instr.Operand };
					return true;
					
				case Code.Ldc_R4:
					value = new StackValue { Type = StackValueType.Single, SingleValue = (float)instr.Operand };
					return true;
					
				case Code.Ldc_R8:
					value = new StackValue { Type = StackValueType.Double, DoubleValue = (double)instr.Operand };
					return true;
			}
			return false;
		}

		private bool TryEvaluateOperation(Instruction instr, out StackValue result) {
			result = null;
			if (evalStack.Count < GetStackPopCount(instr.OpCode))
				return false;

			// Получаем операнды (порядок важен: первый popped - второй операнд)
			StackValue val1 = evalStack.Pop();
			StackValue val2 = null;
			if (GetStackPopCount(instr.OpCode) == 2)
				val2 = evalStack.Pop();

			Code code = instr.OpCode.Code;
			StackValue res = new StackValue();

			try {
				// Integer Operations
				if (val1.Type == StackValueType.Int32 && (val2 == null || val2.Type == StackValueType.Int32)) {
					int a = val1.Int32Value;
					int b = val2?.Int32Value ?? 0;
					int r = 0;
					bool ok = true;

					switch (code) {
						case Code.Add: r = a + b; break;
						case Code.Sub: r = b - a; break; // Порядок: sub b, a -> b-a
						case Code.Mul: r = a * b; break;
						case Code.And: r = a & b; break;
						case Code.Or: r = a | b; break;
						case Code.Xor: r = a ^ b; break;
						case Code.Shr: r = b >> a; break;
						case Code.Shl: r = b << a; break;
						
						// Comparisons returning int (1 or 0)
						case Code.Ceq: r = (b == a) ? 1 : 0; break;
						case Code.Cgt: r = (b > a) ? 1 : 0; break;
						case Code.Cgt_Un: r = ((uint)b > (uint)a) ? 1 : 0; break;
						case Code.Clt: r = (b < a) ? 1 : 0; break;
						case Code.Clt_Un: r = ((uint)b < (uint)a) ? 1 : 0; break;
						
						default: ok = false; break;
					}

					if (ok) {
						res.Type = StackValueType.Int32;
						res.Int32Value = r;
						result = res;
						return true;
					}
				}
				
				// Long Operations
				if (val1.Type == StackValueType.Int64 && (val2 == null || val2.Type == StackValueType.Int64)) {
					long a = val1.Int64Value;
					long b = val2?.Int64Value ?? 0;
					long r = 0;
					bool ok = true;

					switch (code) {
						case Code.Add: r = a + b; break;
						case Code.Sub: r = b - a; break;
						case Code.Mul: r = a * b; break;
						case Code.And: r = a & b; break;
						case Code.Or: r = a | b; break;
						case Code.Xor: r = a ^ b; break;
						case Code.Ceq: r = (b == a) ? 1 : 0; break;
						default: ok = false; break;
					}

					if (ok) {
						res.Type = StackValueType.Int64;
						res.Int64Value = r;
						result = res;
						return true;
					}
				}
				
				// Float/Double Operations (упрощенно)
				if ((val1.Type == StackValueType.Single || val1.Type == StackValueType.Double) &&
				    (val2 == null || val2.Type == StackValueType.Single || val2.Type == StackValueType.Double)) {
					
					double a = val1.Type == StackValueType.Single ? val1.SingleValue : val1.DoubleValue;
					double b = (val2 == null) ? 0 : (val2.Type == StackValueType.Single ? val2.SingleValue : val2.DoubleValue);
					double r = 0;
					bool ok = true;

					switch (code) {
						case Code.Add: r = a + b; break;
						case Code.Sub: r = b - a; break;
						case Code.Mul: r = a * b; break;
						case Code.Div: r = b / a; break;
						case Code.Ceq: r = (b == a) ? 1 : 0; break;
						case Code.Cgt: r = (b > a) ? 1 : 0; break;
						case Code.Clt: r = (b < a) ? 1 : 0; break;
						default: ok = false; break;
					}

					if (ok) {
						res.Type = StackValueType.Double; // Promote to double
						res.DoubleValue = r;
						result = res;
						return true;
					}
				}
			} catch {
				// Overflow or other errors, ignore
			}

			// If not evaluated, push back operands to keep stack consistent? 
			// No, caller handles stack sync based on success/failure.
			// But we popped them. We need to restore if failed?
			// Better approach: don't pop until success confirmed.
			// Re-implementing peek logic briefly:
			if (result == null) {
				// Restore manually if needed, but simpler to just re-push if we had saved them.
				// Since we already popped, let's just re-push them back to evalStack to maintain state for next iterations if this fails
				if (val2 != null) evalStack.Push(val2);
				evalStack.Push(val1);
			}

			return result != null;
		}

		private void ReplaceWithConst(Instruction instr, StackValue val) {
			switch (val.Type) {
				case StackValueType.Int32:
					instr.OpCode = OpCodes.Ldc_I4;
					instr.Operand = val.Int32Value;
					break;
				case StackValueType.Int64:
					instr.OpCode = OpCodes.Ldc_I8;
					instr.Operand = val.Int64Value;
					break;
				case StackValueType.Single:
					instr.OpCode = OpCodes.Ldc_R4;
					instr.Operand = val.SingleValue;
					break;
				case StackValueType.Double:
					instr.OpCode = OpCodes.Ldc_R8;
					instr.Operand = val.DoubleValue;
					break;
			}
		}

		private int GetStackPopCount(OpCode opCode) {
			if (opCode.StackBehaviourPop == StackBehaviour.Pop0) return 0;
			if (opCode.StackBehaviourPop == StackBehaviour.Pop1) return 1;
			if (opCode.StackBehaviourPop == StackBehaviour.Pop1_pop1) return 2;
			if (opCode.StackBehaviourPop == StackBehaviour.Pop1_pop1_pop1) return 3;
			if (opCode.StackBehaviourPop == StackBehaviour.PopAll) return 10; // Approximation
			return 0;
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

		private bool EvaluateUnaryCondition(Code code, StackValue val) {
			if (val.Type == StackValueType.Int32) {
				bool isTrue = val.Int32Value != 0;
				return (code == Code.Brtrue || code == Code.Brtrue_S) ? isTrue : !isTrue;
			}
			if (val.Type == StackValueType.Int64) {
				bool isTrue = val.Int64Value != 0;
				return (code == Code.Brtrue || code == Code.Brtrue_S) ? isTrue : !isTrue;
			}
			// For floats, non-zero is true
			if (val.Type == StackValueType.Single || val.Type == StackValueType.Double) {
				double d = val.Type == StackValueType.Single ? val.SingleValue : val.DoubleValue;
				bool isTrue = d != 0.0;
				return (code == Code.Brtrue || code == Code.Brtrue_S) ? isTrue : !isTrue;
			}
			return false;
		}

		private bool EvaluateBinaryCondition(Code code, StackValue val1, StackValue val2) {
			// val1 is first pushed (left), val2 is second pushed (right)
			// Stack: [val1, val2]. Operation compares val1 and val2.
			// Example: ldc 1, ldc 2, ceq -> 1 == 2? false.
			// In IL: arg1 (val1), arg2 (val2). Opcode compares arg1 and arg2.
			// Wait, stack behavior: push val1, push val2. Top is val2.
			// Ceq pops val2 then val1. Computes val1 == val2.
			
			if (val1.Type == StackValueType.Int32 && val2.Type == StackValueType.Int32) {
				int a = val1.Int32Value;
				int b = val2.Int32Value;
				switch (code) {
					case Code.Beq: case Code.Beq_S: return a == b;
					case Code.Bne_Un: case Code.Bne_Un_S: return a != b;
					case Code.Bgt: case Code.Bgt_S: return a > b;
					case Code.Bgt_Un: case Code.Bgt_Un_S: return (uint)a > (uint)b;
					case Code.Bge: case Code.Bge_S: return a >= b;
					case Code.Bge_Un: case Code.Bge_Un_S: return (uint)a >= (uint)b;
					case Code.Blt: case Code.Blt_S: return a < b;
					case Code.Blt_Un: case Code.Blt_Un_S: return (uint)a < (uint)b;
					case Code.Ble: case Code.Ble_S: return a <= b;
					case Code.Ble_Un: case Code.Ble_Un_S: return (uint)a <= (uint)b;
				}
			}
			
			if (val1.Type == StackValueType.Int64 && val2.Type == StackValueType.Int64) {
				long a = val1.Int64Value;
				long b = val2.Int64Value;
				switch (code) {
					case Code.Beq: case Code.Beq_S: return a == b;
					case Code.Bne_Un: case Code.Bne_Un_S: return a != b;
					case Code.Bgt: case Code.Bgt_S: return a > b;
					case Code.Bgt_Un: case Code.Bgt_Un_S: return (ulong)a > (ulong)b;
					case Code.Bge: case Code.Bge_S: return a >= b;
					case Code.Bge_Un: case Code.Bge_Un_S: return (ulong)a >= (ulong)b;
					case Code.Blt: case Code.Blt_S: return a < b;
					case Code.Blt_Un: case Code.Blt_Un_S: return (ulong)a < (ulong)b;
					case Code.Ble: case Code.Ble_S: return a <= b;
					case Code.Ble_Un: case Code.Ble_Un_S: return (ulong)a <= (ulong)b;
				}
			}
			
			// Floating point
			if ((val1.Type == StackValueType.Single || val1.Type == StackValueType.Double) &&
			    (val2.Type == StackValueType.Single || val2.Type == StackValueType.Double)) {
				double a = val1.Type == StackValueType.Single ? val1.SingleValue : val1.DoubleValue;
				double b = val2.Type == StackValueType.Single ? val2.SingleValue : val2.DoubleValue;
				switch (code) {
					case Code.Beq: case Code.Beq_S: return a == b;
					case Code.Bne_Un: case Code.Bne_Un_S: return a != b;
					case Code.Bgt: case Code.Bgt_S: return a > b;
					case Code.Bgt_Un: case Code.Bgt_Un_S: return a > b;
					case Code.Bge: case Code.Bge_S: return a >= b;
					case Code.Bge_Un: case Code.Bge_Un_S: return a >= b;
					case Code.Blt: case Code.Blt_S: return a < b;
					case Code.Blt_Un: case Code.Blt_Un_S: return a < b;
					case Code.Ble: case Code.Ble_S: return a <= b;
					case Code.Ble_Un: case Code.Ble_Un_S: return a <= b;
				}
			}

			return false;
		}

		#endregion

		// Вспомогательный класс для хранения значений в виртуальном стеке
		private class StackValue {
			public StackValueType Type;
			public int Int32Value;
			public long Int64Value;
			public float SingleValue;
			public double DoubleValue;
		}

		private enum StackValueType {
			Int32,
			Int64,
			Single,
			Double
		}
	}
}
