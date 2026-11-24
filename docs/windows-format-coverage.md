# Windows 앱의 메일 형식과 원본 대조

Windows 앱 1.6.0은 Rust 판독기를 포함합니다. ANSI PST와 기존 C# 판독 범위 밖의 PST 변형은 Rust 판독기로 엽니다. DBX·MBX, OLM, EMLX, MMDF·Babyl·Rmail, ZIP, 독립 TNEF, CMS SignedData도 같은 자료 열기 화면에서 처리합니다. Maildir와 MH는 원래 폴더 구조를 유지한 채 폴더를 선택합니다. Outlook Express 보조 색인과 MH 시퀀스는 자료 분석 결과에 표시합니다.

| 자료 | 원본을 표시하는 기준 |
| --- | --- |
| EMLX·MMDF·Babyl·MBX·일부 CMS | 원본 파일에서 읽은 범위입니다. EMLX의 길이 접두사와 후행 plist는 메시지 범위에서 제외합니다. |
| DBX | 메시지 인덱스에 연결된 분할 구간을 순서대로 읽습니다. HEX는 저장 항목 기준 오프셋을 표시하고 매니페스트에는 구성 구간을 기록합니다. |
| ZIP·OLM | 내부 항목의 이름, 번호, 항목 안의 범위를 기록합니다. HEX 오프셋은 압축을 풀어 읽은 항목을 기준으로 합니다. |
| 부분 EMLX | 조건이 맞는 외부 파일의 경로와 SHA-256을 함께 기록합니다. 이후 외부 파일이 바뀌면 저장 시 다시 대조합니다. |
| ANSI PST와 내장 MSG | PST의 폴더·메시지 NID, MAPI 속성 또는 MSG의 CFB 저장소·스트림을 표시합니다. 내장 MSG를 독립 MSG 파일로 다시 조립하지 않습니다. |

읽을 수 있는 헤더·본문·첨부는 검색하거나 내보낼 수 있습니다. 확인 가능한 원본 바이트는 HEX나 근거 묶음에서 대조할 수 있습니다. 기존 CLI의 프로필 전용 수집 명령과 Outlook Express 파일 사이의 관계 보고서는 별도로 유지됩니다. 확장 판독 경로의 메일에서 구조 이상을 검사할 수 없는 첨부는 판독 범위에 제약이 있습니다. EDB와 NSF는 형식 식별까지 적용합니다. CMS 서명의 검증과 복호화는 지원하지 않습니다.

## 확인한 자료

- 공개 Outlook Express DBX·MBX, Outlook for Mac OLM, Microsoft PST SDK ANSI 14 PST, 독립 TNEF 및 CMS SignedData.
- 합성 ZIP, EMLX·부분 EMLX와 외부 첨부, MMDF, Babyl, Maildir, MH, NWS.
- .NET 검증 29건에서 기존 EML·MBOX·MSG·PST·OST와 확장 포맷의 읽기·저장·재검증을 확인했습니다.
- 격리된 Windows 화면에서 ZIP 내용을 검색하고, 헤더 HEX와 첨부 원본 위치를 대조한 뒤 첨부를 내보냈습니다. EMLX는 길이 접두사를 제외한 실제 파일 위치로 열었습니다. 기존 EML·MBOX·MSG 화면 검증도 통과했습니다.
- Windows x64 경량판과 런타임 포함판을 각각 실행해 EMLX를 분석하고 제목이 메일 목록에 표시되는 것을 확인했습니다.

[.NET 교차 포맷 검증](../tests/MailViewer.Verification/Program.Formats.cs) · [Windows 화면 검증](../tools/FlowSmoke/Program.Formats.cs) · [Rust CLI 검증과 표본 출처](rust-mail-cli.md#검증)
