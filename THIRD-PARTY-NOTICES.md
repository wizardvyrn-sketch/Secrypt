# Third-party notices / サードパーティのライセンス表記

Secrypt itself is licensed under the MIT License (see [LICENSE](LICENSE)). The distributed package also contains the following third-party software, each under its own license, reproduced below.

Secrypt 本体のライセンスは MIT です（[LICENSE](LICENSE)）。配布パッケージには、次のサードパーティのソフトウェアも含まれます。それぞれのライセンスの原文を以下に示します。

## Summary / 一覧

| Component | Version | Purpose | License |
|---|---|---|---|
| NSec.Cryptography | 24.4.0 | XChaCha20-Poly1305・Argon2id（暗号） | MIT |
| libsodium（NuGet パッケージ） | 1.0.19 | NSec が使う暗号ライブラリ本体（ネイティブ DLL） | ISC |
| QRCoder | 1.6.0 | 鍵ファイルの QR コード生成 | MIT |
| Microsoft.TSS（TSS.MSR） | 2.1.1 | TPM 2.0 とのやり取り | MIT（TSS.MSR の LICENSE） |
| CommunityToolkit.Mvvm | 8.4.0 | 画面の ViewModel（MVVM） | MIT |
| Microsoft.Extensions.DependencyInjection / Logging / Options / Primitives（各 Abstractions を含む） | 10.0.10 | 依存の組み立て・ログ | MIT |
| .NET runtime and WPF (self-contained; bundled in the distributed package) | 10.0 | 実行環境・画面 | MIT |
| Oxanium（Bold・Regular） | — | 起動時ロゴ画面の見出し | SIL Open Font License 1.1 |
| Share Tech Mono | — | 起動時ロゴ画面のラベル・数値 | SIL Open Font License 1.1 |

## NSec.Cryptography 24.4.0

License: MIT

```text
MIT License

Copyright (c) 2024 Klaus Hartke

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

## libsodium（NuGet パッケージ） 1.0.19

License: ISC

```text
/*
 * ISC License
 *
 * Copyright (c) 2013-2023
 * Frank Denis <j at pureftpd dot org>
 *
 * Permission to use, copy, modify, and/or distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */
```

## QRCoder 1.6.0

License: MIT

```text
The MIT License (MIT)

Copyright (c) 2013-2025 Raffael Herrmann
Copyright (c) 2024-2025 Shane Krueger

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Microsoft.TSS（TSS.MSR） 2.1.1

License: MIT（TSS.MSR の LICENSE）

```text
The copyright in this software is being made available under the MIT License,
included below. This software may be subject to other third party and
contributor rights, including patent rights, and no such rights are granted
under this license.

Copyright (c) Microsoft Corporation. All rights reserved.

The MIT License (MIT)

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

## CommunityToolkit.Mvvm 8.4.0

License: MIT

```text
# .NET Community Toolkit

Copyright © .NET Foundation and Contributors

All rights reserved.

## MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Microsoft.Extensions.* 10.0.10 / .NET runtime and WPF 10.0

License: MIT. Copyright (c) .NET Foundation and Contributors / Microsoft Corporation. The license text is the standard MIT License shown above for CommunityToolkit.Mvvm (same terms; copyright holder as stated here). Source: https://github.com/dotnet/runtime , https://github.com/dotnet/wpf

## Fonts / フォント（SIL Open Font License 1.1）

The fonts are embedded in the application. The full license texts are included in the distributed package under `app\Fonts\`. フォントはアプリに埋め込まれています。ライセンスの全文は、配布パッケージの `app\Fonts\` にあります。

- **Oxanium（Bold・Regular）** — Copyright 2019 The Oxanium Project Authors (https://github.com/sevmeyer/oxanium)  
  Full text: `src/PwManager.App/Fonts/Oxanium-OFL.txt`
- **Share Tech Mono** — Copyright (c) 2012, Carrois Type Design, Ralph du Carrois (post@carrois.com www.carrois.com), with Reserved Font Name 'Share'  
  Full text: `src/PwManager.App/Fonts/ShareTechMono-OFL.txt`
