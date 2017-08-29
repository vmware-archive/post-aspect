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

using System;

namespace PostAspect
{
    /// <summary>
    /// Base aspect for all aspect interception implementation
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Constructor, Inherited = true)]
    public abstract class BaseAspect : Attribute
    {
        /// <summary>
        /// Method called prior to invoking any method that is decorated with an aspect or its class is decorated
        /// </summary>
        /// <param name="methodInfo">MethodInfo</param>
        public abstract void OnEnter(AspectMethodInfo methodInfo);

        /// <summary>
        /// Method called prior to leaving any method that is decorated with an aspect or its class is decorated
        /// </summary>
        /// <param name="methodInfo">MethodInfo</param>
        public virtual void OnExit(AspectMethodInfo methodInfo) { }

        /// <summary>
        /// Method called after an exception has occurred in any method that is decorated with an aspect or its class is decorated
        /// </summary>
        /// <param name="methodInfo">MethodInfo</param>
        public virtual void OnError(AspectMethodInfo methodInfo) { }

        /// <summary>
        /// Method called after a successfully execution of any method that is decorated with an aspect or its class is decorated
        /// </summary>
        /// <param name="methodInfo"></param>
        public virtual void OnSuccess(AspectMethodInfo methodInfo) { }
    }
}
