﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ArduinoCsCompiler.Runtime;
using Iot.Device.Common;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1591
namespace ArduinoCsCompiler
{
    public class ExecutionSet
    {
        internal const int GenericTokenStep = 0x0100_0000;
        private const int StringTokenStep = 0x0001_0000;
        private const int NullableToken = 0x0080_0000;
        public const int GenericTokenMask = -8_388_608; // 0xFF80_0000 as signed

        public static ExecutionSet? CompiledKernel = null;

        private static readonly SnapShot EmptySnapShot = new SnapShot(null, new List<int>(), new List<int>(), new List<int>());

        private readonly MicroCompiler _compiler;
        private readonly List<ArduinoMethodDeclaration> _methods;
        private readonly List<ClassDeclaration> _classes;
        private readonly Dictionary<TypeInfo, int> _patchedTypeTokens;
        private readonly Dictionary<int, TypeInfo> _inversePatchedTypeTokens;
        private readonly Dictionary<MethodBase, int> _patchedMethodTokens;
        private readonly Dictionary<int, MethodBase> _inversePatchedMethodTokens; // Same as the above, but the other way round
        private readonly Dictionary<FieldInfo, (int Token, byte[]? InitializerData)> _patchedFieldTokens;
        private readonly Dictionary<int, FieldInfo> _inversePatchedFieldTokens;
        private readonly HashSet<(Type Original, Type Replacement, bool Subclasses)> _classesReplaced;

        /// <summary>
        /// For each method name, the list of replacements. The outer dictionary is to speed up lookups
        /// </summary>
        private readonly Dictionary<string, List<(MethodBase, MethodBase?)>> _methodsReplaced;
        // These classes (and any of their methods) will not be loaded, even if they seem in use. This should speed up testing
        private readonly List<Type> _classesToSuppress;
        // String data, already UTF-8 encoded. The StringData value is actually only used for debugging purposes
        private readonly List<(int Token, byte[] EncodedString, string StringData)> _strings;
        private readonly CompilerSettings _compilerSettings;
        private readonly List<(int Token, TypeInfo? Class)> _specialTypeList;
        private readonly ILogger _logger;

        private int _numDeclaredMethods;
        private ArduinoTask _entryPoint;
        private int _nextToken;
        private int _nextGenericToken;
        private int _nextStringToken;
        private SnapShot _kernelSnapShot;
        private Dictionary<Type, MethodInfo> _arrayListImpl;

        internal ExecutionSet(MicroCompiler compiler, CompilerSettings compilerSettings)
        {
            _compiler = compiler;
            _logger = this.GetCurrentClassLogger();
            _methods = new List<ArduinoMethodDeclaration>();
            _classes = new List<ClassDeclaration>();
            _patchedTypeTokens = new Dictionary<TypeInfo, int>();
            _patchedMethodTokens = new Dictionary<MethodBase, int>();
            _patchedFieldTokens = new Dictionary<FieldInfo, (int Token, byte[]? InitializerData)>();
            _inversePatchedMethodTokens = new Dictionary<int, MethodBase>();
            _inversePatchedTypeTokens = new Dictionary<int, TypeInfo>();
            _inversePatchedFieldTokens = new Dictionary<int, FieldInfo>();
            _classesReplaced = new HashSet<(Type Original, Type Replacement, bool Subclasses)>();
            _methodsReplaced = new();
            _classesToSuppress = new List<Type>();
            _arrayListImpl = new();
            _strings = new();
            _specialTypeList = new();

            _nextToken = (int)KnownTypeTokens.LargestKnownTypeToken + 1;
            _nextGenericToken = GenericTokenStep * 4; // The first entries are reserved (see KnownTypeTokens)
            _nextStringToken = StringTokenStep; // The lower 16 bit are the length

            _numDeclaredMethods = 0;
            _entryPoint = null!;
            _kernelSnapShot = EmptySnapShot;
            _compilerSettings = compilerSettings.Clone();
            MainEntryPointInternal = null;
            TokenOfStartupMethod = 0;
        }

        internal ExecutionSet(ExecutionSet setToClone, MicroCompiler compiler, CompilerSettings compilerSettings)
        {
            _compiler = compiler;
            _logger = this.GetCurrentClassLogger();
            if (setToClone._compilerSettings != compilerSettings)
            {
                throw new NotSupportedException("Target compiler settings must be equal to existing");
            }

            _methods = new List<ArduinoMethodDeclaration>(setToClone._methods);
            _classes = new List<ClassDeclaration>(setToClone._classes);

            _patchedTypeTokens = new Dictionary<TypeInfo, int>(setToClone._patchedTypeTokens);
            _patchedMethodTokens = new Dictionary<MethodBase, int>(setToClone._patchedMethodTokens);
            _patchedFieldTokens = new Dictionary<FieldInfo, (int Token, byte[]? InitializerData)>(setToClone._patchedFieldTokens);
            _inversePatchedMethodTokens = new Dictionary<int, MethodBase>(setToClone._inversePatchedMethodTokens);
            _inversePatchedTypeTokens = new Dictionary<int, TypeInfo>(setToClone._inversePatchedTypeTokens);
            _inversePatchedFieldTokens = new Dictionary<int, FieldInfo>(setToClone._inversePatchedFieldTokens);
            _classesReplaced = new HashSet<(Type Original, Type Replacement, bool Subclasses)>(setToClone._classesReplaced);
            _methodsReplaced = new(setToClone._methodsReplaced);
            _classesToSuppress = new List<Type>(setToClone._classesToSuppress);
            _strings = new(setToClone._strings);
            _arrayListImpl = new Dictionary<Type, MethodInfo>(setToClone._arrayListImpl);
            _specialTypeList = new(setToClone._specialTypeList);

            _nextToken = setToClone._nextToken;
            _nextGenericToken = setToClone._nextGenericToken;
            _nextStringToken = setToClone._nextStringToken;

            _numDeclaredMethods = setToClone._numDeclaredMethods;
            _entryPoint = setToClone._entryPoint;
            _kernelSnapShot = setToClone._kernelSnapShot;
            _compilerSettings = compilerSettings.Clone();
            if (setToClone.FirmwareStartupSequence != null)
            {
                FirmwareStartupSequence = new List<IlCode>(setToClone.FirmwareStartupSequence);
            }

            MainEntryPointInternal = setToClone.MainEntryPointInternal;
            TokenOfStartupMethod = setToClone.TokenOfStartupMethod;
        }

        internal IList<ClassDeclaration> Classes => _classes;

        public ArduinoTask MainEntryPoint
        {
            get => _entryPoint;
            internal set => _entryPoint = value;
        }

        internal MethodInfo? MainEntryPointInternal
        {
            get;
            set;
        }

        internal Dictionary<Type, MethodInfo> ArrayListImplementation
        {
            get
            {
                return _arrayListImpl;
            }
        }

        public CompilerSettings CompilerSettings => _compilerSettings;

        /// <summary>
        /// The list of methods that need to be called to start the code (static constructors and the main method)
        /// The sequence is combined into a startup method if the program is to run directly from flash, otherwise <see cref="MicroCompiler.ExecuteStaticCtors"/> takes care of the
        /// sequencing.
        /// </summary>
        internal List<IlCode>? FirmwareStartupSequence { get; set; }

        public int TokenOfStartupMethod { get; set; }

        private static int CalculateTotalStringSize(List<(int Token, byte[] EncodedString, string StringData)> strings, SnapShot fromSnapShot, SnapShot toSnapShot)
        {
            int totalSize = sizeof(int); // we need a trailing empty entry
            var list = strings.Where(x => !fromSnapShot.AlreadyAssignedStringTokens.Contains(x.Token) && toSnapShot.AlreadyAssignedStringTokens.Contains(x.Token));
            foreach (var elem in list)
            {
                totalSize += sizeof(int);
                totalSize += elem.EncodedString.Length;
            }

            return totalSize;
        }

        public void Load(bool runStaticCtors)
        {
            if (CompilerSettings.ForceFlashWrite)
            {
                _compiler.ClearAllData(true, true);
            }

            if (CompilerSettings.CreateKernelForFlashing)
            {
                if (!_compiler.BoardHasKernelLoaded(_kernelSnapShot))
                {
                    // Perform a full flash erase (since the above also returns false if a wrong kernel is loaded)
                    _compiler.ClearAllData(true, true);
                    _compiler.SendClassDeclarations(this, EmptySnapShot, _kernelSnapShot, true);
                    _compiler.SendMethods(this, EmptySnapShot, _kernelSnapShot, true);
                    List<(int Token, byte[] Data, string NoData)> converted = new();
                    // Need to do this manually, due to stupid nullability conversion restrictions
                    foreach (var elem in _patchedFieldTokens.Values)
                    {
                        if (elem.InitializerData != null)
                        {
                            converted.Add((elem.Token, elem.InitializerData, string.Empty));
                        }
                    }

                    _compiler.SendConstants(converted, EmptySnapShot, _kernelSnapShot, true);
                    _compiler.CopyToFlash();

                    int totalStringSize = CalculateTotalStringSize(_strings, EmptySnapShot, _kernelSnapShot);
                    _compiler.PrepareStringLoad(0, totalStringSize); // The first argument is currently unused
                    _compiler.SendStrings(_strings.ToList(), EmptySnapShot, _kernelSnapShot, true);
                    _compiler.SendSpecialTypeList(_specialTypeList.Select(x => x.Token).ToList(), EmptySnapShot, _kernelSnapShot, true);
                    _compiler.CopyToFlash();

                    // The kernel contains no startup method, therefore don't use one
                    _compiler.WriteFlashHeader(_kernelSnapShot, 0, CodeStartupFlags.None);
                }
            }
            else if (!CompilerSettings.UseFlashForProgram && !CompilerSettings.UseFlashForKernel)
            {
                // If flash is not used, we must make sure it's empty. Otherwise there will be conflicts.
                _compiler.ClearAllData(true, true);
            }

            Load(_kernelSnapShot, CreateSnapShot(), runStaticCtors);
        }

        private void Load(SnapShot from, SnapShot to, bool runStaticCtos)
        {
            if (MainEntryPointInternal == null)
            {
                throw new InvalidOperationException("Main entry point not defined");
            }

            bool doWriteProgramToFlash = CompilerSettings.DoCopyToFlash(false);

            if (!_compiler.BoardHasKernelLoaded(to))
            {
                if (from == EmptySnapShot || doWriteProgramToFlash)
                {
                    _compiler.ClearAllData(true, true);
                }
                else
                {
                    _compiler.ClearAllData(true, false);
                }

                _compiler.SetExecutionSetActive(this);
                _logger.LogInformation("1/5 Uploading class declarations...");
                _compiler.SendClassDeclarations(this, from, to, false);
                _logger.LogInformation("2/5 Uploading methods..");
                _compiler.SendMethods(this, from, to, false);
                List<(int Token, byte[] Data, string NoData)> converted = new();
                // Need to do this manually, due to stupid nullability conversion restrictions
                foreach (var elem in _patchedFieldTokens.Values)
                {
                    if (elem.InitializerData != null)
                    {
                        converted.Add((elem.Token, elem.InitializerData, string.Empty));
                    }
                }

                _logger.LogInformation("3/5 Uploading constants...");
                _compiler.SendConstants(converted, from, to, false);
                if (doWriteProgramToFlash)
                {
                    _compiler.CopyToFlash();
                }

                int totalStringSize = CalculateTotalStringSize(_strings, from, to);
                _compiler.PrepareStringLoad(0, totalStringSize); // The first argument is currently unused
                _logger.LogInformation("4/5 Uploading strings...");
                _compiler.SendStrings(_strings.ToList(), from, to, false);
                _logger.LogInformation("5/5 Uploading special types...");
                _compiler.SendSpecialTypeList(_specialTypeList.Select(x => x.Token).ToList(), from, to, false);
                _logger.LogInformation("Finalizing...");
                if (doWriteProgramToFlash)
                {
                    _compiler.WriteFlashHeader(to, TokenOfStartupMethod, CompilerSettings.AutoRestartProgram ? CodeStartupFlags.AutoRestartAfterCrash : CodeStartupFlags.None);
                }

                _logger.LogInformation("Upload successfully completed");
            }
            else
            {
                // We need to activate this execution set even if we don't need to load anything
                _compiler.SetExecutionSetActive(this);
            }

            MainEntryPoint = _compiler.GetTask(this, MainEntryPointInternal);

            if (runStaticCtos)
            {
                // Execute all static ctors
                _compiler.ExecuteStaticCtors(this);
            }
        }

        public ArduinoTask GetTaskForMethod<T>(T mainEntryPoint)
            where T : Delegate
        {
            return _compiler.GetTask(this, mainEntryPoint.Method);
        }

        /// <summary>
        /// Creates a snapshot of the execution set. Used to identify parts that are pre-loaded (or shall be?)
        /// </summary>
        internal SnapShot CreateSnapShot()
        {
            List<int> tokens = new List<int>();
            List<int> stringTokens = new List<int>();
            // Can't use this, because the list may contain replacement tokens for methods we haven't actually picked as part of this snapshot
            // tokens.AddRange(_patchedMethodTokens.Values);
            tokens.AddRange(_methods.Select(x => x.Token));
            tokens.AddRange(_patchedFieldTokens.Values.Where(x => x.InitializerData != null).Select(x => x.Token));
            tokens.AddRange(_patchedTypeTokens.Values);
            stringTokens.AddRange(_strings.Select(x => x.Token));

            return new SnapShot(this, tokens, stringTokens, _specialTypeList.Select(x => x.Token).ToList());
        }

        internal void CreateKernelSnapShot()
        {
            _compiler.FinalizeExecutionSet(this, true);

            _kernelSnapShot = CreateSnapShot();
        }

        internal void SuppressType(Type t)
        {
            _classesToSuppress.Add(t);
        }

        internal void SuppressType(string name)
        {
            var t = Type.GetType(name, true);
            _classesToSuppress.Add(t!);
        }

        public long EstimateRequiredMemory()
        {
            return EstimateRequiredMemory(out _);
        }

        public long EstimateRequiredMemory(out List<KeyValuePair<Type, ClassStatistics>> details)
        {
            const int MethodBodyMinSize = 40;
            Dictionary<Type, ClassStatistics> classSizes = new Dictionary<Type, ClassStatistics>();
            long bytesUsed = 0;
            foreach (var cls in Classes)
            {
                int classBytes = 40;
                classBytes += cls.StaticSize;
                classBytes += cls.Members.Count * 8; // Assuming 32 bit target system for now
                foreach (var field in cls.Members)
                {
                    if (_inversePatchedFieldTokens.TryGetValue(field.Token, out FieldInfo? value))
                    {
                        if (_patchedFieldTokens.TryGetValue(value, out var data))
                        {
                            classBytes += data.InitializerData?.Length ?? 0;
                        }
                    }
                }

                bytesUsed += classBytes;
                classSizes[cls.TheType] = new ClassStatistics(cls, classBytes);
            }

            foreach (var method in _methods)
            {
                int methodBytes = MethodBodyMinSize;
                methodBytes += method.ArgumentCount * 4;
                methodBytes += method.MaxLocals * 4;

                methodBytes += method.Code.IlBytes != null ? method.Code.IlBytes.Length : 0;

                var type = method.MethodBase.DeclaringType!;
                if (classSizes.TryGetValue(type, out _))
                {
                    classSizes[type].MethodBytes += methodBytes;
                    classSizes[type].Methods.Add((method, methodBytes));
                }
                else
                {
                    classSizes[type] = new ClassStatistics(new ClassDeclaration(type, 0, 0, 0, new List<ClassMember>(), new List<Type>()), 0);
                    classSizes[type].MethodBytes += methodBytes;
                    classSizes[type].Methods.Add((method, methodBytes));
                }

                bytesUsed += methodBytes;
            }

            foreach (var stat in classSizes.Values)
            {
                stat.TotalBytes = stat.ClassBytes + stat.MethodBytes;
            }

            details = classSizes.OrderByDescending(x => x.Value.TotalBytes).ToList();

            foreach (var constant in _strings)
            {
                bytesUsed += constant.EncodedString.Length + 4;
            }

            return bytesUsed;
        }

        public sealed class ClassStatistics
        {
            public ClassStatistics(ClassDeclaration type, int classBytes)
            {
                Type = type;
                ClassBytes = classBytes;
                MethodBytes = 0;
                TotalBytes = 0;
                Methods = new List<(ArduinoMethodDeclaration, int)>();
            }

            public ClassDeclaration Type
            {
                get;
            }

            public int ClassBytes
            {
                get;
            }

            public int MethodBytes
            {
                get;
                set;
            }

            public int TotalBytes
            {
                get;
                set;
            }

            internal List<(ArduinoMethodDeclaration Method, int Size)> Methods
            {
                get;
            }

            public override string ToString()
            {
                return $"Class {Type.Name} uses {MethodBytes} for code and {ClassBytes} for fields and metadata. Total {MethodBytes}.";
            }
        }

        internal int GetOrAddMethodToken(MethodBase methodBase, MethodBase callingMethod)
        {
            int token;
            if (_patchedMethodTokens.TryGetValue(methodBase, out token))
            {
                return token;
            }

            var replacement = GetReplacement(methodBase, callingMethod);
            if (replacement != null)
            {
                return GetOrAddMethodToken(replacement, callingMethod);
            }

            // If the class is being replaced, search the replacement class
            var classReplacement = GetReplacement(methodBase.DeclaringType);
            if (classReplacement != null && replacement == null)
            {
                replacement = GetReplacement(methodBase, callingMethod, classReplacement);
                if (replacement == null)
                {
                    // If the replacement class has a static method named "NotSupportedException", we call this instead (expecting that this will never be called).
                    // This is used so we can remove all the unsupported implementations for compiler intrinsics.
                    MethodInfo? dummyMethod = GetNotSupportedExceptionMethod(classReplacement);
                    if (dummyMethod != null)
                    {
                        return GetOrAddMethodToken(dummyMethod, callingMethod);
                    }

                    throw new InvalidOperationException($"Internal error: Expected replacement not found for {methodBase.MemberInfoSignature()}");
                }

                return GetOrAddMethodToken(replacement, callingMethod);
            }

            token = _nextToken++;
            _patchedMethodTokens.Add(methodBase, token);
            _inversePatchedMethodTokens.Add(token, methodBase);
            return token;
        }

        /// <summary>
        /// Returns non-null when the whole method should be removed, because it's assumed that it's never going to be called
        /// </summary>
        private static MethodInfo? GetNotSupportedExceptionMethod(Type classReplacement)
        {
            var dummyMethod = classReplacement.GetMethod(nameof(MiniX86Intrinsics.NotSupportedException), BindingFlags.Public | BindingFlags.Static);
            return dummyMethod;
        }

        internal int GetOrAddFieldToken(FieldInfo field)
        {
            string temp = field.Name;
            if (_patchedFieldTokens.TryGetValue(field, out var token))
            {
                return token.Token;
            }

            // If both the original class and the replacement have fields, match them and define the original as the "correct" ones
            // There shouldn't be a problem if only either one contains a field (but check size calculation!)
            if (MicroCompiler.HasReplacementAttribute(field.DeclaringType!, out var attrib))
            {
                var replacementType = attrib!.TypeToReplace!;
                var replacementField = replacementType.GetField(field.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (replacementField != null)
                {
                    if (_patchedFieldTokens.TryGetValue(replacementField, out token))
                    {
                        return token.Token;
                    }

                    field = replacementField;
                }
            }

            int tk = _nextToken++;
            _patchedFieldTokens.Add(field, (tk, null));
            _inversePatchedFieldTokens.Add(tk, field);
            return tk;
        }

        internal int GetOrAddFieldToken(FieldInfo field, byte[] initializerData)
        {
            if (_patchedFieldTokens.TryGetValue(field, out var token))
            {
                // Even though the token can already exist, we need to add the data (this happens typically only once, but the field may have several uses)
                var newEntry = (token.Token, initializerData);
                _patchedFieldTokens[field] = newEntry;
                return newEntry.Token;
            }

            int tk = _nextToken++;

            _patchedFieldTokens.Add(field, (tk, initializerData));
            _inversePatchedFieldTokens.Add(tk, field);
            return tk;
        }

        /// <summary>
        /// Creates the new, application-global token for a class. Some bits have special meaning:
        /// 0..23 Type id for ordinary classes (neither generics nor nullables)
        /// 24 True if this is a Nullable{T}
        /// 25..31 Type id for generic classes
        /// Combinations are constructed: if the token 0x32 means "int", 0x0200_0000 means "IEquatable{T}", then 0x0200_0032 is IEquatable{int} and
        /// 0x0280_0032 is Nullable{IEquatable{int}} (not IEquatable{Nullable{int}}!) Since Nullable{T} only works with value types, this isn't normally used.
        /// There's a special list of very complex tokens that is used for complex combinations that can't be mapped with the above bit combinations, namely
        /// objects with multiple template arguments such as List{List{T}} or Dictionary{TKey, TValue}
        /// </summary>
        /// <param name="typeInfo">Original type to add to list</param>
        /// <returns>A new token for the given type, or the existing token if it is already in the list</returns>
        internal int GetOrAddClassToken(TypeInfo typeInfo)
        {
            int token;
            if (_patchedTypeTokens.TryGetValue(typeInfo, out token))
            {
                return token;
            }

            var replacement = GetReplacement(typeInfo);
            if (replacement != null)
            {
                return GetOrAddClassToken(replacement.GetTypeInfo());
            }

            if (typeInfo == typeof(object))
            {
                token = (int)KnownTypeTokens.Object;
            }
            else if (typeInfo == typeof(UInt32))
            {
                token = (int)KnownTypeTokens.Uint32;
            }
            else if (typeInfo == typeof(Int32))
            {
                token = (int)KnownTypeTokens.Int32;
            }
            else if (typeInfo == typeof(UInt64))
            {
                token = (int)KnownTypeTokens.Uint64;
            }
            else if (typeInfo == typeof(Int64))
            {
                token = (int)KnownTypeTokens.Int64;
            }
            else if (typeInfo == typeof(byte))
            {
                token = (int)KnownTypeTokens.Byte;
            }
            else if (typeInfo == typeof(System.Delegate))
            {
                token = (int)KnownTypeTokens.Delegate;
            }
            else if (typeInfo == typeof(System.MulticastDelegate))
            {
                token = (int)KnownTypeTokens.MulticastDelegate;
            }
            else if (typeInfo.FullName == "System.Enum")
            {
                // Note that enums are value types, but "System.Enum" itself is not.
                token = (int)KnownTypeTokens.Enum;
            }
            else if (typeInfo == typeof(TypeInfo))
            {
                token = (int)KnownTypeTokens.TypeInfo;
            }
            else if (typeInfo == typeof(string) || typeInfo == typeof(MiniString))
            {
                token = (int)KnownTypeTokens.String;
            }
            else if (typeInfo.FullName == "System.RuntimeType")
            {
                token = (int)KnownTypeTokens.RuntimeType;
            }
            else if (typeInfo == typeof(Type) || typeInfo == typeof(MiniType))
            {
                token = (int)KnownTypeTokens.Type;
            }
            else if (typeInfo == typeof(Array) || typeInfo == typeof(MiniArray))
            {
                token = (int)KnownTypeTokens.Array;
            }
            else if (typeInfo.FullName != null &&
                     typeInfo.FullName.StartsWith("System.ByReference`1[[System.Byte, System.Private.CoreLib,", StringComparison.Ordinal)) // Ignore version of library
            {
                token = (int)KnownTypeTokens.ByReferenceByte;
            }
            else if (typeInfo == typeof(Nullable<>))
            {
                token = NullableToken;
            }
            else if (typeInfo == typeof(IEnumerable<>))
            {
                token = (int)KnownTypeTokens.IEnumerableOfT;
            }
            else if (typeInfo == typeof(Span<>))
            {
                token = (int)KnownTypeTokens.SpanOfT;
            }
            else if (typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                // Nullable<T>, but with a defined T
                int baseToken = GetOrAddClassToken(typeInfo.GetGenericArguments()[0].GetTypeInfo());
                token = baseToken + NullableToken;
            }
            else if (typeInfo.IsGenericTypeDefinition)
            {
                // Type with (at least one) incomplete type argument.
                // We use token values > 24 bit, so that we can add it to the base type to implement MakeGenericType(), at least for single type arguments
                token = _nextGenericToken;
                _nextGenericToken += GenericTokenStep;
            }
            else if (typeInfo.IsGenericType)
            {
                // complete generic type, find whether the base definition has been defined already
                var definition = typeInfo.GetGenericTypeDefinition();
                int definitionToken = GetOrAddClassToken(definition.GetTypeInfo());
                Type[] typeArguments = typeInfo.GetGenericArguments();
                Type firstArg = typeArguments.First();
                int firstArgToken = GetOrAddClassToken(firstArg.GetTypeInfo());
                if (firstArg.IsGenericType || typeArguments.Length > 1)
                {
                    // If the first argument is itself generic or there is more than one generic argument, we need to create extended metadata
                    // The list consists of a length element, then the master token (which we create here) and then the tokens of the type arguments
                    List<(int Token, TypeInfo? Element)> entries = new List<(int Token, TypeInfo? Element)>(); // Element is only for debugging purposes

                    // one entry for the length, one entry for the combined token, one for the left part.
                    // We can't use the FF's as marker, because the list itself might contain them when combining very complex tokens.
                    entries.Add((typeArguments.Length + 3, null));
                    token = (int)(0xFF000000 | _nextToken++); // Create a new token, marked "special" (top 8 bits set).
                    // Note: While in theory, this element could again be wrapped in a Nullable<>, this is probably really rare, as generic types are almost never structs, therefore
                    // a token such as 0x02800079 is rather IList<Nullable<int>> rather than Nullable<IList<int>>, but getting that right everywhere is difficult
                    entries.Add((token, typeInfo)); // own type
                    entries.Add((definitionToken, definition.GetTypeInfo())); // The generic type
                    foreach (var t in typeArguments)
                    {
                        var info = t.GetTypeInfo();
                        int token2 = GetOrAddClassToken(info);
                        entries.Add((token2, info));
                    }

                    _specialTypeList.AddRange(entries);
                }
                else
                {
                    // Our token is the combination of the generic type and the only argument. This allows a simple implementation for Type.MakeGenericType() with
                    // generic types with a single argument
                    token = definitionToken + firstArgToken;
                }
            }
            else
            {
                token = _nextToken++;
            }

            _patchedTypeTokens.Add(typeInfo, token);
            if (!_inversePatchedTypeTokens.ContainsKey(token))
            {
                _inversePatchedTypeTokens.Add(token, typeInfo);
            }
            else
            {
                // This can only happen for replacement classes that won't be fully replaced. InverseResolveToken shall return the original in this case(?)
                if (typeInfo.GetCustomAttributes((typeof(ArduinoReplacementAttribute)), false).Length == 0)
                {
                    _inversePatchedTypeTokens[token] = typeInfo;
                }
            }

            return token;
        }

        internal bool AddClass(ClassDeclaration type)
        {
            if (_classesToSuppress.Contains(type.TheType))
            {
                return false;
            }

            if (_classes.Any(x => x.TheType == type.TheType))
            {
                return false;
            }

            if (_classesReplaced.Any(x => x.Original == type.TheType))
            {
                throw new InvalidOperationException($"Class {type} should have been replaced by its replacement");
            }

            _classes.Add(type);
            _logger.LogDebug($"Class {type.TheType.MemberInfoSignature(true)} added to the execution set with token 0x{type.NewToken:X}");
            return true;
        }

        internal bool IsSuppressed(Type t)
        {
            return _classesToSuppress.Contains(t);
        }

        internal bool HasDefinition(Type classType)
        {
            if (_classesToSuppress.Contains(classType))
            {
                return true;
            }

            if (_classes.Any(x => x.TheType == classType))
            {
                return true;
            }

            return false;
        }

        internal bool HasMethod(MemberInfo m, MethodBase callingMethod, out IlCode? found)
        {
            if (_classesToSuppress.Contains(m.DeclaringType!))
            {
                found = null;
                return true;
            }

            var replacement = GetReplacement((MethodBase)m, callingMethod);
            if (replacement != null)
            {
                m = replacement;
            }

            var find = _methods.FirstOrDefault(x => AreMethodsIdentical(x.MethodBase, (MethodBase)m));
            found = find?.Code;
            return find != null;
        }

        private bool AreMethodsIdentical(MethodBase a, MethodBase b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (!MicroCompiler.MethodsHaveSameSignature(a, b))
            {
                return false;
            }

            if (a.IsGenericMethod && b.IsGenericMethod)
            {
                var typeParamsa = a.GetGenericArguments();
                var typeParamsb = b.GetGenericArguments();
                if (typeParamsa.Length != typeParamsb.Length)
                {
                    return false;
                }

                for (int i = 0; i < typeParamsa.Length; i++)
                {
                    if (typeParamsa[i] != typeParamsb[i])
                    {
                        return false;
                    }
                }
            }

            if (a.DeclaringType!.IsConstructedGenericType && b.DeclaringType!.IsConstructedGenericType)
            {
                var typeParamsa = a.DeclaringType.GetGenericArguments();
                var typeParamsb = b.DeclaringType.GetGenericArguments();
                if (typeParamsa.Length != typeParamsb.Length)
                {
                    return false;
                }

                for (int i = 0; i < typeParamsa.Length; i++)
                {
                    if (typeParamsa[i] != typeParamsb[i])
                    {
                        return false;
                    }
                }
            }

            if (a.MetadataToken == b.MetadataToken && a.Module.FullyQualifiedName == b.Module.FullyQualifiedName)
            {
                return true;
            }

            return false;
        }

        internal bool AddMethod(ArduinoMethodDeclaration method)
        {
            if (_numDeclaredMethods >= Math.Pow(2, 14) - 1)
            {
                // In practice, the maximum will be much less on most Arduino boards, due to ram limits
                throw new NotSupportedException("To many methods declared. Only 2^14 supported.");
            }

            // These conditions allow some memory optimization in the runtime. It's very rare that methods exceed these limitations.
            if (method.MaxLocals > 255)
            {
                throw new NotSupportedException("Methods with more than 255 local variables are unsupported");
            }

            if (method.MaxStack > 255)
            {
                throw new NotSupportedException("The maximum execution stack size is 255");
            }

            if (_methods.Any(x => AreMethodsIdentical(x.MethodBase, method.MethodBase)))
            {
                return false;
            }

            if (_methodsReplaced.TryGetValue(method.Name, out var list))
            {
                if (list.Any(x => AreMethodsIdentical(x.Item1, method.MethodBase)))
                {
                    throw new InvalidOperationException(
                        $"Method {method} should have been replaced by its replacement");
                }

                if (list.Any(x => AreMethodsIdentical(x.Item1, method.MethodBase) && x.Item2 == null))
                {
                    throw new InvalidOperationException(
                        $"The method {method} should be replaced, but has no new implementation. This program will not execute");
                }
            }

            _methods.Add(method);
            method.Index = _numDeclaredMethods;
            _numDeclaredMethods++;

            if ((method.Flags & MethodFlags.SpecialMethod) == MethodFlags.SpecialMethod)
            {
                _logger.LogDebug($"Internally implemented method {method.MethodBase.MethodSignature(false)} added to the execution set with token 0x{method.Token:X}");
            }
            else
            {
                _logger.LogDebug($"Method {method.MethodBase.MethodSignature(false)} added to the execution set with token 0x{method.Token:X}");
            }

            return true;
        }

        internal IList<ArduinoMethodDeclaration> Methods()
        {
            return _methods;
        }

        internal MemberInfo? InverseResolveToken(int token)
        {
            // Todo: This is very slow - consider inversing the dictionaries during data prep
            if (_inversePatchedMethodTokens.TryGetValue(token, out var method))
            {
                return method;
            }

            if (_inversePatchedFieldTokens.TryGetValue(token, out var field))
            {
                return field;
            }

            if (_inversePatchedTypeTokens.TryGetValue(token, out var t))
            {
                return t;
            }

            // Try whether the input token is a constructed generic token, which was not expected in this combination
            if (_inversePatchedTypeTokens.TryGetValue((int)(token & 0xFF00_0000), out t))
            {
                try
                {
                    if (t != null && t.IsGenericTypeDefinition)
                    {
                        if (_inversePatchedTypeTokens.TryGetValue((int)(token & 0x00FF_FFFF), out var t2))
                        {
                            return t.MakeGenericType(t2);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore, try next approach (if any)
                }
            }

            return null;
        }

        internal void AddReplacementType(Type? typeToReplace, Type replacement, bool includingSubclasses, bool includingPrivates)
        {
            if (typeToReplace == null)
            {
                throw new ArgumentNullException(nameof(typeToReplace));
            }

            if (!_classesReplaced.Add((typeToReplace, replacement, includingSubclasses)))
            {
                return;
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;

            if (!includingSubclasses)
            {
                flags |= BindingFlags.DeclaredOnly;
            }

            List<MethodInfo> methodsNeedingReplacement = typeToReplace.GetMethods(flags).ToList();

            if (!includingPrivates)
            {
                // We can't include internal methods by the filter above, so (unless we need all) remove all privates here, keeping public and internals
                methodsNeedingReplacement = methodsNeedingReplacement.Where(x => !x.IsPrivate).ToList();
            }

            foreach (var methoda in replacement.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                // Above, we only check the public methods, here we also look at the private ones
                BindingFlags otherFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
                if (!includingSubclasses)
                {
                    otherFlags |= BindingFlags.DeclaredOnly;
                }

                bool replacementFound = false;
                foreach (var methodb in typeToReplace.GetMethods(otherFlags))
                {
                    if (!methodsNeedingReplacement.Contains(methodb))
                    {
                        // This is not the one in need of replacement (or already has one, if the parameters matched a similar implementation as well)
                        continue;
                    }

                    if (MicroCompiler.MethodsHaveSameSignature(methoda, methodb) || MicroCompiler.AreSameOperatorMethods(methoda, methodb))
                    {
                        // Method A shall replace Method B
                        AddReplacementMethod(methodb, methoda);
                        // Remove from the list - so we see in the end what is missing
                        methodsNeedingReplacement.Remove(methodb);
                        replacementFound = true;
                        break;
                    }
                }

                if (!replacementFound)
                {
                    _logger.LogWarning($"Method {methoda.MemberInfoSignature()} has nothing to replace");
                }
            }

            // Add these as "not implemented" to the list, so we can figure out what we actually need
            foreach (var m in methodsNeedingReplacement)
            {
                AddReplacementMethod(m, null);
            }

            // And do the same as above for all (public) ctors
            var ctorsNeedingReplacement = typeToReplace.GetConstructors(BindingFlags.Public | BindingFlags.Instance).ToList();

            foreach (var methoda in replacement.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                // Above, we only check the public methods, here we also look at the private ones
                foreach (var methodb in typeToReplace.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (MicroCompiler.MethodsHaveSameSignature(methoda, methodb))
                    {
                        // Method A shall replace Method B
                        AddReplacementMethod(methodb, methoda);
                        // Remove from the list - so we see in the end what is missing
                        ctorsNeedingReplacement.Remove(methodb);
                        break;
                    }
                }
            }

            foreach (var m in ctorsNeedingReplacement)
            {
                AddReplacementMethod(m, null);
            }
        }

        internal Type? GetReplacement(Type? original)
        {
            if (original == null)
            {
                return null;
            }

            foreach (var x in _classesReplaced)
            {
                if (x.Original == original)
                {
                    return x.Replacement;
                }
                else if (x.Subclasses && original.IsSubclassOf(x.Original))
                {
                    // If we need to replace all subclasses of x as well, we need to add them here to the replacement list, because
                    // we initially didn't know which classes will be in this set.
                    AddReplacementType(original, x.Replacement, true, false);
                    return x.Replacement;
                }
            }

            return null;
        }

        internal MethodBase? GetReplacement(MethodBase original, MethodBase callingMethod)
        {
            // Odd: I'm pretty sure that previously equality on MethodBase instances worked, but for some reason not all instances pointing to the same method are Equal().
            if (!_methodsReplaced.TryGetValue(original.Name, out var methodsToConsider))
            {
                return null;
            }

            var elem = methodsToConsider.FirstOrDefault(x =>
            {
                if (x.Item2 != null && MicroCompiler.HasArduinoImplementationAttribute(x.Item2, out var attrib) && attrib.IgnoreGenericTypes)
                {
                    // There are only very few methods with the IgnoreGenericTypes attribute. Therefore a simple test is enough
                    if (x.Item1.Name == original.Name && MicroCompiler.HasReplacementAttribute(x.Item2.DeclaringType!, out var replacementAttribute)
                                                      && replacementAttribute.TypeToReplace == original.DeclaringType)
                    {
                        return true;
                    }
                }

                return AreMethodsIdentical(x.Item1, original);
            });

            if (elem.Item1 == default)
            {
                return null;
            }
            else if (elem.Item2 == null)
            {
                var classReplacement = GetReplacement(elem.Item1.DeclaringType);
                if (GetNotSupportedExceptionMethod(classReplacement!) != null)
                {
                    return null;
                }

                throw new InvalidOperationException($"Should have a replacement for {original.MethodSignature()}, but it is missing. Caller: {callingMethod.MethodSignature()}");
            }

            return elem.Item2;
        }

        /// <summary>
        /// Try to find a replacement for the given method in the given class
        /// </summary>
        /// <param name="methodInfo">The method to replace</param>
        /// <param name="callingMethod">The method that called into this one</param>
        /// <param name="classToSearch">With a method in this class</param>
        /// <returns></returns>
        internal MethodBase? GetReplacement(MethodBase methodInfo, MethodBase callingMethod, Type classToSearch)
        {
            foreach (var replacementMethod in classToSearch.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (MicroCompiler.MethodsHaveSameSignature(replacementMethod, methodInfo) || MicroCompiler.AreSameOperatorMethods(replacementMethod, methodInfo))
                {
                    if (!replacementMethod.IsGenericMethodDefinition)
                    {
                        return replacementMethod;
                    }
                }

                if (replacementMethod.Name == methodInfo.Name && replacementMethod.GetParameters().Length == methodInfo.GetParameters().Length &&
                    methodInfo.IsConstructedGenericMethod && replacementMethod.IsGenericMethodDefinition &&
                    methodInfo.GetGenericArguments().Length == replacementMethod.GetGenericArguments().Length)
                {
                    // The replacement method is likely the correct one, but we need to instantiate it.
                    var repl = replacementMethod.MakeGenericMethod(methodInfo.GetGenericArguments());
                    if (MicroCompiler.MethodsHaveSameSignature(repl, methodInfo) || MicroCompiler.AreSameOperatorMethods(repl, methodInfo))
                    {
                        return repl;
                    }
                }
            }

            return null; // this is now likely an error
        }

        internal void AddReplacementMethod(MethodBase? toReplace, MethodBase? replacement)
        {
            if (toReplace == null)
            {
                throw new ArgumentNullException(nameof(toReplace));
            }

            if (replacement != null && AreMethodsIdentical(toReplace, replacement))
            {
                // Replacing a method with itself may happen if virtual resolution points back to the same base class. Should fix itself later.
                return;
            }

            string name = toReplace.Name;
            if (_methodsReplaced.TryGetValue(toReplace.Name, out var list))
            {
                list.Add((toReplace, replacement));
            }
            else
            {
                list = new List<(MethodBase, MethodBase?)>();
                list.Add((toReplace, replacement));
                _methodsReplaced.Add(toReplace.Name, list);
            }
        }

        internal int GetOrAddString(string data)
        {
            var encoded = Encoding.UTF8.GetBytes(data);
            var existing = _strings.FirstOrDefault(x => x.EncodedString.SequenceEqual(encoded));
            if (existing.Token != 0)
            {
                return existing.Token;
            }

            int token = _nextStringToken + encoded.Length;
            _nextStringToken += StringTokenStep;
            _strings.Add((token, encoded, data));
            return token;
        }

        internal ArduinoMethodDeclaration GetMethod(MethodBase methodInfo)
        {
            return _methods.First(x => AreMethodsIdentical(x.MethodBase, methodInfo));
        }

        private static int Xor(IEnumerable<int> inputs)
        {
            int result = 0;
            foreach (int i in inputs)
            {
                result ^= i;
            }

            return result;
        }

        public sealed class SnapShot
        {
            private readonly ExecutionSet? _set;

            public SnapShot(ExecutionSet? set, List<int> alreadyAssignedTokens, List<int> alreadyAssignedStringTokens, List<int> specialTypes)
            {
                AlreadyAssignedTokens = alreadyAssignedTokens;
                AlreadyAssignedStringTokens = alreadyAssignedStringTokens;
                SpecialTypes = specialTypes;
                _set = set;
            }

            public List<int> AlreadyAssignedTokens
            {
                get;
            }

            public List<int> AlreadyAssignedStringTokens
            {
                get;
            }

            public List<int> SpecialTypes
            {
                get;
            }

            public override int GetHashCode()
            {
                int ret = AlreadyAssignedTokens.Count;
                ret ^= Xor(AlreadyAssignedTokens);
                ret ^= Xor(AlreadyAssignedStringTokens);
                if (AlreadyAssignedTokens.Count > 0 && _set != null)
                {
                    // Add the original token from the last entry in the list (could also add all of them)
                    var element = _set.InverseResolveToken(AlreadyAssignedTokens[AlreadyAssignedTokens.Count - 1]);
                    ret ^= element!.MetadataToken;
                }

                return ret;
            }
        }

        /// <summary>
        /// This is required for the special implementation of Array.GetEnumerator that is an implementation of IList{T}
        /// See I.8.9.1 Array types. T[] implements IList{T}.
        /// </summary>
        public void AddArrayImplementation(TypeInfo arrayType, MethodInfo getEnumeratorCall)
        {
            _arrayListImpl[arrayType] = getEnumeratorCall;
        }

        public void SuppressNamespace(string namespacePrefix, bool includingSubNamespaces)
        {
            var assembly = typeof(System.Runtime.GCSettings).Assembly;
            foreach (var t in assembly.GetTypes())
            {
                if (t.Namespace == null)
                {
                    continue;
                }

                if (includingSubNamespaces)
                {
                    if (t.Namespace.StartsWith(namespacePrefix, StringComparison.InvariantCulture))
                    {
                        SuppressType(t);
                    }
                }
                else
                {
                    if (t.Namespace == namespacePrefix)
                    {
                        SuppressType(t);
                    }
                }
            }
        }
    }
}