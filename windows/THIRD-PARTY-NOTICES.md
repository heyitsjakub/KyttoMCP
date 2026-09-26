# Third-party notices

Kytto MCP is licensed under the MIT License (see [`LICENSE`](LICENSE)). It
incorporates or redistributes the components below, each under its own license.

## cl100k_base vocabulary (tiktoken)

`src/Kytto.Core/Tokenizer/cl100k_base.tiktoken` is the `cl100k_base` BPE rank
table published for OpenAI's [tiktoken](https://github.com/openai/tiktoken). It
is embedded in `Kytto.Core` to estimate token weight (`docs/design.md` §7.4). The
pre-tokenization pattern in `BpeTokenizer.cs` and the expected token ids in
`tests/Kytto.Core.Tests/Fixtures/cl100k_vectors.json` are derived from it.

```
MIT License

Copyright (c) 2022 OpenAI, Shantanu Jain

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Microsoft Edge WebView2 SDK

`Microsoft.Web.WebView2` (NuGet) is referenced by `Kytto.App`, and its
assemblies ship with release builds. It is distributed under the following
license; the package's own `NOTICE.txt` lists the components it incorporates
and must accompany binary redistributions.

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## .NET runtime

Release builds are published self-contained and include the .NET runtime, WPF
and Windows Forms, which are MIT-licensed by the .NET Foundation and
contributors. The runtime's own `THIRD-PARTY-NOTICES.TXT` applies to those
files.

## Build and test tools

Not redistributed: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk, coverlet and
System.Management (MIT), used by the test projects only, and Inno Setup, which
builds the installer.

## Trademarks

Claude and Claude Code are trademarks of Anthropic. Cursor is a trademark of
Anysphere. Visual Studio Code and Windows are trademarks of Microsoft. Codex is
a trademark of OpenAI. Kytto MCP is an independent project and is not affiliated
with or endorsed by any of them. Client icons are read at runtime from the
applications installed on the user's machine and are not bundled in this
repository.
