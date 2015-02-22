﻿using System;
using System.Collections.Generic;
namespace Rubberduck.UnitTesting
{
    public interface ITestEngine
    {
        IDictionary<TestMethod, TestResult> AllTests { get; set; }
        IEnumerable<TestMethod> FailedTests();
        IEnumerable<TestMethod> LastRunTests(TestOutcome? outcome = null);
        IEnumerable<TestMethod> NotRunTests();
        IEnumerable<TestMethod> PassedTests();

        void Run();
        void Run(System.Collections.Generic.IEnumerable<TestMethod> tests);

        event EventHandler<TestCompleteEventArg> TestComplete;
    }

    public class TestCompleteEventArg : EventArgs
    {
        public TestResult Result { get; private set; }
        public TestMethod Test { get; private set; }

        public TestCompleteEventArg(TestMethod test, TestResult result)
        {
            this.Test = test;
            this.Result = result;
        }
    }
}
