﻿using System;
using Iot.Device.Arduino;

namespace ArduinoCsCompiler.Runtime
{
    [ArduinoReplacement(typeof(System.Math), false)]
    internal class MiniMath
    {
        [ArduinoImplementation("MathCeiling", 100)]
        public static double Ceiling(double a)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathFloor", 101)]
        public static double Floor(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathPow", 102)]
        public static double Pow(double x, double y)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathLog", 103)]
        public static double Log(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathLog2", 104)]
        public static double Log2(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathLog10", 105)]
        public static double Log10(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathSin", 106)]
        public static double Sin(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathCos", 107)]
        public static double Cos(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathTan", 108)]
        public static double Tan(double d)
        {
            throw new NotImplementedException();
        }

        [ArduinoImplementation("MathSqrt", 109)]
        public static double Sqrt(double d)
        {
            return Math.Sqrt(d);
        }

        [ArduinoImplementation("MathExp", 110)]
        public static double Exp(double d)
        {
            return Math.Exp(d);
        }

        [ArduinoImplementation("MathAbs", 111)]
        public static double Abs(double d)
        {
            return Math.Abs(d);
        }
    }
}
