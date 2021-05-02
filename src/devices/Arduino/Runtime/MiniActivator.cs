﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Iot.Device.Arduino.Runtime
{
    [ArduinoReplacement(typeof(System.Activator), IncludingPrivates = true)]
    internal static class MiniActivator
    {
        [ArduinoImplementation(NativeMethod.ActivatorCreateInstance)]
        public static object? CreateInstance(Type type, BindingFlags bindingAttr, Binder? binder, object?[]? args, CultureInfo? culture, object?[]? activationAttributes)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation]
        public static object? CreateInstance(Type type, bool nonPublic, bool wrapExceptions)
        {
            return CreateInstance(type, nonPublic ? BindingFlags.CreateInstance | BindingFlags.NonPublic | BindingFlags.Public : BindingFlags.CreateInstance | BindingFlags.Public,
                null, new object?[0], CultureInfo.CurrentCulture, null);
        }
    }
}