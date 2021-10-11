﻿using System;
using Iot.Device.Arduino;

namespace ArduinoCsCompiler.Runtime
{
    [ArduinoReplacement(typeof(System.Delegate), IncludingPrivates = true)]
    internal class MiniDelegate
    {
        [ArduinoImplementation(NativeMethod.DelegateInternalEqualTypes)]
        public static bool InternalEqualTypes(object a, object b)
        {
            throw new NotImplementedException();
        }
    }
}
