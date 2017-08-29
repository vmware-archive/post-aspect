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
using System.Collections.Generic;
using System.Reflection;

namespace PostAspect
{
    /// <summary>
    /// Class for enscapsulating information regarding method parameters such as name, value, attributes and etc
    /// </summary>
    public sealed class AspectMethodParameterInfo
    {
        /// <summary>
        /// Initialize an instance of <see cref="AspectMethodParameterInfo"/>
        /// </summary>
        public AspectMethodParameterInfo()
        {
            Attributes = new List<Attribute>();
        }

        /// <summary>
        /// Gets or sets Name of parameter
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets Value of parameter
        /// </summary>
        public object Value { get; set; }

        /// <summary>
        /// Gets or sets whether value of parameter is reference type
        /// </summary>
        public bool IsRef { get; set; }

        /// <summary>
        /// Gets or sets parameter Type
        /// </summary>
        public Type Type { get; set; }

        /// <summary>
        /// Gets or sets parameter attributes
        /// </summary>
        public IList<Attribute> Attributes { get; set; }
    }

    /// <summary>
    /// Class encapsulating method context information capture at the execution point of methods decorated with aspects either directly or through declaring class
    /// </summary>
    public sealed class AspectMethodInfo
    {
        /// <summary>
        /// Initialize an instance of <see cref="AspectMethodInfo"/>
        /// </summary>
        public AspectMethodInfo()
        {
            Arguments = new List<object>();
        }

        /// <summary>
        /// Gets or sets flag indicating whether exception caught during method execution should rethrow or surpressed
        /// </summary>
        public bool ReThrow { get; set; } = true;

        /// <summary>
        /// Gets or set flag indicating if current method execution should occur. When set, exit event for aspects will still be invoked
        /// </summary>
        public bool Continue { get; set; } = true;

        /// <summary>
        /// Gets or sets reflection method data that can used to provide further information about current method context
        /// </summary>
        public MethodBase Method { get; set; }

        /// <summary>
        /// Gets or set the current instance that initiate current method context execution. This value will be null when method is static
        /// </summary>
        public object Instance { get; set; }

        /// <summary>
        /// Gets or sets returns of the current method context for non-void methods
        /// </summary>
        public object Returns { get; set; }

        /// <summary>
        /// Gets or sets arguments that was passed on the current method context for execution. 
        /// </summary>
        public List<object> Arguments { get; set; }

        /// <summary>
        /// Gets or sets parameter information for the current method context. This will contain parameter name, value, attribute, and e.t.c
        /// </summary>
        public IList<AspectMethodParameterInfo> Parameters { get; set; }

        /// <summary>
        /// Gets or sets the attributes collected from both method and method's class during execution of current method context
        /// </summary>
        public IList<Attribute> Attributes { get; set; }

        /// <summary>
        /// Gets or sets the exception that was captured during error interception for the current method context
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Gets or sets tag of method used for sharing context between various entrypoint of aspect methods (enter/exit/etc) for a given method execution scope
        /// </summary>
        public object Tag { get; set; }
    }
}
