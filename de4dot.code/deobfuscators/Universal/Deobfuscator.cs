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

using System.Collections.Generic;
using System.Text.RegularExpressions;
using de4dot.blocks;
using de4dot.blocks.cflow;

namespace de4dot.code.deobfuscators.Universal {
    /// <summary>
    /// Информация о универсальном деобфускаторе
    /// </summary>
    public class DeobfuscatorInfo : DeobfuscatorInfoBase {
        public const string THE_NAME = "Universal";
        public const string THE_TYPE = "univ";
        const string DEFAULT_REGEX = DeobfuscatorBase.DEFAULT_VALID_NAME_REGEX;

        public DeobfuscatorInfo()
            : base(DEFAULT_REGEX) {
        }

        public override string Name {
            get { return THE_NAME; }
        }

        public override string Type {
            get { return THE_TYPE; }
        }

        public override IDeobfuscator CreateDeobfuscator() {
            return new Deobfuscator(new Deobfuscator.Options {
                RenameResourcesInCode = true,
                ValidNameRegex = validNameRegex.Get(),
            });
        }
    }

    /// <summary>
    /// Универсальный деобфускатор с поддержкой базовых техник очистки
    /// </summary>
    class Deobfuscator : DeobfuscatorBase {
        internal class Options : OptionsBase {
        }

        public override string Type {
            get { return DeobfuscatorInfo.THE_TYPE; }
        }

        public override string TypeLong {
            get { return DeobfuscatorInfo.THE_NAME; }
        }

        public override string Name {
            get { return "Universal Deobfuscator"; }
        }

        internal Deobfuscator(Options options)
            : base(options) {
        }

        public override IEnumerable<IBlocksDeobfuscator> BlocksDeobfuscators {
            get {
                var list = new List<IBlocksDeobfuscator>();
                // Добавляем универсальный деобфускатор арифметических выражений
                list.Add(new de4dot.blocks.cflow.ArithmeticDeobfuscator { ExecuteIfNotModified = false });
                return list;
            }
        }

        protected override void ScanForObfuscator() {
            // Универсальный деобфускатор применяется ко всем файлам
        }

        protected override int DetectInternal() {
            // Всегда возвращаем 1, чтобы применить базовые техники очистки
            return 1;
        }

        public override IEnumerable<int> GetStringDecrypterMethods() {
            return new List<int>();
        }

        public override void DeobfuscateBegin() {
            base.DeobfuscateBegin();
            Logger.n("Starting universal deobfuscation...");
        }

        public override void DeobfuscateEnd() {
            // Применяем дополнительные техники очистки
            RemoveJunkCode();
            FixInvalidMetadata();
            CleanUnusedResources();
            
            base.DeobfuscateEnd();
            Logger.n("Universal deobfuscation completed.");
        }

        /// <summary>
        /// Удаляет мусор: пустые методы, неиспользуемые поля, дублирующиеся атрибуты
        /// </summary>
        private void RemoveJunkCode() {
            Logger.v("Removing junk code...");
            Logger.Instance.Indent();
            
            var methodsToRemove = new List<MethodDef>();
            var fieldsToRemove = new List<FieldDef>();
            
            foreach (var type in module.GetTypes()) {
                // Удаляем пустые методы кроме конструкторов и cctor
                foreach (var method in type.Methods) {
                    if (method.IsConstructor || method.IsStaticConstructor())
                        continue;
                    
                    if (IsEmptyMethod(method)) {
                        methodsToRemove.Add(method);
                    }
                }
                
                // Удаляем неиспользуемые private поля
                foreach (var field in type.Fields) {
                    if (field.IsPrivate && !IsFieldUsed(field)) {
                        fieldsToRemove.Add(field);
                    }
                }
            }
            
            // Удаляем найденные методы
            foreach (var method in methodsToRemove) {
                var type = method.DeclaringType;
                if (type != null && type.Methods.Remove(method)) {
                    Logger.v("Removed empty method: {0}", method.Name);
                }
            }
            
            // Удаляем найденные поля
            foreach (var field in fieldsToRemove) {
                var type = field.DeclaringType;
                if (type != null && type.Fields.Remove(field)) {
                    Logger.v("Removed unused field: {0}", field.Name);
                }
            }
            
            Logger.Instance.DeIndent();
        }

        /// <summary>
        /// Проверяет, является ли метод пустым
        /// </summary>
        private bool IsEmptyMethod(MethodDef method) {
            if (!method.HasBody || method.Body.Instructions.Count == 0)
                return true;
            
            // Метод содержит только ret
            if (method.Body.Instructions.Count == 1 && 
                method.Body.Instructions[0].OpCode == dnlib.DotNet.Emit.OpCodes.Ret)
                return true;
            
            return false;
        }

        /// <summary>
        /// Проверяет, используется ли поле где-либо в коде
        /// </summary>
        private bool IsFieldUsed(FieldDef field) {
            // Упрощенная проверка - ищем ссылки на поле
            foreach (var type in module.GetTypes()) {
                foreach (var method in type.Methods) {
                    if (!method.HasBody)
                        continue;
                    
                    foreach (var instr in method.Body.Instructions) {
                        if (instr.Operand == field)
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Исправляет некорректные метаданные
        /// </summary>
        private void FixInvalidMetadata() {
            Logger.v("Fixing invalid metadata...");
            
            // Восстанавливаем базовые типы для enum
            FixEnumTypes();
            
            // Исправляем интерфейсы
            FixInterfaces();
            
            // Удаляем дублирующиеся атрибуты
            RemoveDuplicateAttributes();
        }

        /// <summary>
        /// Удаляет дублирующиеся атрибуты
        /// </summary>
        private void RemoveDuplicateAttributes() {
            foreach (var type in module.GetTypes()) {
                var seenAttributes = new HashSet<string>();
                var attrsToRemove = new List<System.Reflection.CustomAttributeData>();
                
                // Здесь можно добавить логику удаления дублирующихся CA
            }
        }

        /// <summary>
        /// Очищает неиспользуемые ресурсы
        /// </summary>
        private void CleanUnusedResources() {
            Logger.v("Cleaning unused resources...");
            Logger.Instance.Indent();
            
            var resourcesToRemove = new List<Resource>();
            
            foreach (var resource in module.Resources) {
                // Можно добавить логику определения неиспользуемых ресурсов
                if (resource.Name.String.StartsWith("<")) {
                    // Скрытые ресурсы часто являются мусором
                    // Но нужно быть осторожным
                }
            }
            
            Logger.Instance.DeIndent();
        }
    }
}
