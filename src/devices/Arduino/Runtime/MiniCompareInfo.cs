﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Iot.Device.Arduino.Runtime
{
    [ArduinoReplacement(typeof(CompareInfo), false, IncludingPrivates = true)]
    internal class MiniCompareInfo
    {
        [ArduinoImplementation]
        public void IcuInitSortHandle()
        {
        }

        [ArduinoImplementation]
        public int IcuCompareString(ReadOnlySpan<char> string1, ReadOnlySpan<char> string2, CompareOptions options)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation]
        private unsafe int IcuIndexOfCore(ReadOnlySpan<char> source, ReadOnlySpan<char> target, CompareOptions options, int* matchLengthPtr, bool fromBeginning)
        {
            throw new NotImplementedException();
        }
    }
}