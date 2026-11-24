# 첨부파일 미리보기

**첨부파일** 탭에서 파일을 고르면 오른쪽에 미리보기가 열립니다. 목록과 미리보기 사이의 경계를 드래그해 폭을 조절할 수 있습니다. **파일 정보**에는 크기, MIME 유형, SHA-256과 원본 위치가 표시됩니다.

| 첨부 | 표시 범위 |
| --- | --- |
| PDF, HWP, HWPX, DOCX | 첫 페이지 |
| JPG, JPEG, PNG | 이미지 |
| TXT, CSV 등 텍스트 | 앞 32 KiB. UTF 계열과 CP949 지원 |
| BIN, DAT | 문자로 읽기 어려우면 처음 8 KiB의 표시 가능한 ASCII를 보여 줍니다. |

문서·이미지에 사용하는 Rust 워커는 네트워크 권한을 부여하지 않은 Windows AppContainer에서 실행합니다. 워커는 첨부 바이트를 받아 최대 변 1,600px의 픽셀 데이터를 반환합니다. 문서 글꼴은 시스템에 있는 글꼴로 대체될 수 있습니다.

- 첨부 입력은 최대 24 MiB입니다.
- ZIP은 항목 4,096개와 헤더에 기록된 압축 해제 크기 합계 96 MiB까지 처리합니다.
- 워커의 메모리 한도는 448 MiB입니다. 렌더러 준비와 응답에는 8초 제한을 적용합니다.

**HEX**는 저장된 MIME 범위, MSG 스트림 또는 PST·OST 속성을 엽니다. 첨부 구조 이상에서 여는 HEX의 오프셋은 디코딩한 첨부를 기준으로 합니다. [첨부 구조 대조](../docs/attachment-signatures.md)

## 빌드

Windows x64에서 저장소 루트를 기준으로 실행합니다. 배포 스크립트는 워커를 Brotli로 압축해 앱에 포함합니다.

```powershell
$env:RUSTFLAGS = '-C target-feature=+crt-static'
cargo build --manifest-path preview/Cargo.toml --release --locked
pwsh -NoProfile -File tools/publish-mail-evidence.ps1 -OutputDirectory artifacts/mail-evidence -Variant Both
```

[렌더러 의존성](Cargo.toml) · [라이선스 고지](../licenses/mail-attachment-preview.txt) · [미리보기 화면 시험](../tools/FlowSmoke/Program.ReaderAttachments.cs)
