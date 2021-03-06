// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Common.Utilities
{
    using System;
    using System.Text;

    /// <summary>
    /// Exception utilities.
    /// </summary>
    public class ExceptionUtilities
    {
        /// <summary>
        /// Returns an exception message with all inner exceptions messages.
        /// </summary>
        /// <param name="exception"> The exception. </param>
        /// <returns> The formatted string message of the exception. </returns>
        public static string GetExceptionMessage(Exception exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var exceptionString = new StringBuilder(exception.Message);
            var inner = exception.InnerException;
            while (inner != null)
            {
                exceptionString.Append(Environment.NewLine);
                exceptionString.Append(inner.Message);
                inner = inner.InnerException;
            }

            return exceptionString.ToString();
        }
    }
}
