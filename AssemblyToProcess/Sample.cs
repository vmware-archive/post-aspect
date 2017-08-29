/*
 * Copyright (c) 2017 VMware, Inc. All Rights Reserved.
 * 
 * Licensed under the MIT License, Version 2.0 (the "License"); 
 * You may not use this file except in compliance with the License. 
 * You may obtain a copy of the License at 
 * 
 *     https://opensource.org/licenses/MIT
 *  
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using PostAspect;
using System;
using System.Threading.Tasks;

namespace AssemblyToProcess
{
    public class LogAspect : BaseAspect
    {
        public override void OnEnter(AspectMethodInfo methodInfo)
        {
            var attributes = methodInfo.Attributes;
            methodInfo.Tag = "This is a test";
            Console.WriteLine("Entered {0}", methodInfo.Method.Name);
        }

        public override void OnExit(AspectMethodInfo methodInfo)
        {
            Console.WriteLine("Got: {0}", methodInfo.Tag);
            Console.WriteLine("Exit {0}", methodInfo.Method.Name);
        }

        public override void OnError(AspectMethodInfo methodInfo)
        {
            base.OnError(methodInfo);
        }
    }

    [LogAspect]
    public class Sample
    {
        public void TestMethod()
        {
            Console.WriteLine("Sample2:TestMethod => {0}", GetValue().Result);
        }

        public async Task<int> GetValue()
        {
            return await Task.FromResult(100);
        }
    }
}
