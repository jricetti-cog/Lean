/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Text;
using System.Text.Encodings.Web;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Json string encoder that escapes the same characters Newtonsoft.Json did: control characters,
    /// quotes, backslashes and the javascript line terminators. Everything else is written as is.
    /// </summary>
    public sealed class LeanJsonEncoder : JavaScriptEncoder
    {
        /// <summary>
        /// The shared instance
        /// </summary>
        public static LeanJsonEncoder Instance { get; } = new();

        private LeanJsonEncoder()
        {
        }

        /// <summary>
        /// Maximum number of characters a single character can be escaped into
        /// </summary>
        public override int MaxOutputCharactersPerInputCharacter => 6;

        /// <summary>
        /// Determines whether the given unicode scalar needs escaping
        /// </summary>
        public override bool WillEncode(int unicodeScalar)
        {
            return unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\'
                || unicodeScalar == 0x85 || unicodeScalar == 0x2028 || unicodeScalar == 0x2029;
        }

        /// <summary>
        /// Finds the index of the first character that needs escaping
        /// </summary>
        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
            {
                if (WillEncode(text[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Escapes the given unicode scalar into the buffer
        /// </summary>
        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            ReadOnlySpan<char> escaped = unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                _ => null
            };
            Span<char> scratch = stackalloc char[6];
            if (escaped.IsEmpty)
            {
                if (WillEncode(unicodeScalar))
                {
                    scratch[0] = '\\';
                    scratch[1] = 'u';
                    ((ushort)unicodeScalar).TryFormat(scratch.Slice(2), out _, "x4");
                    escaped = scratch;
                }
                else
                {
                    var length = new Rune(unicodeScalar).EncodeToUtf16(scratch);
                    escaped = scratch.Slice(0, length);
                }
            }
            if (escaped.Length > bufferLength)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            escaped.CopyTo(new Span<char>(buffer, bufferLength));
            numberOfCharactersWritten = escaped.Length;
            return true;
        }
    }
}
