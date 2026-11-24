# 첨부파일 자동 미리보기

첨부파일 탭은 파일 목록과 오른쪽 미리보기 창으로 나뉩니다. 선택을 바꾸면 같은 자리에 다른 첨부가 표시됩니다. 가운데 경계를 드래그하거나 선택한 뒤 방향키를 눌러 두 영역의 폭을 조절합니다. 읽기 영역이 좁으면 파일 선택 상자에 별도 줄을 할당합니다. 본문에서 파일 형식 아이콘이 붙은 이름을 누르면 해당 첨부가 선택됩니다.

사진은 전체가 보이는 크기로, 문서는 너비에 맞춘 첫 페이지로 열립니다. 미리보기 위에는 너비 맞춤과 전체 맞춤을 전환하는 아이콘, 미리보기를 확대하거나 목록을 복원하는 아이콘이 있습니다. 도구 설명은 마우스를 올리거나 Tab으로 이동하면 표시됩니다. `크게 보기`는 메일 목록과 폴더 자리까지 사용하고, `Esc`나 `목록과 함께 보기`로 돌아가면 선택한 첨부 행이 다시 보입니다. `파일 정보`는 미리보기 하단에 있으며, 창 높이가 충분하면 처음부터 펼칩니다. 전체 파일명, 크기, MIME 유형, SHA-256과 원본 위치는 이곳에서 확인합니다.

첨부 도구 모음의 **HEX**는 저장된 MIME 범위, MSG 첨부 스트림 또는 PST·OST 첨부 속성을 엽니다. 버튼의 도구 설명에는 선택한 첨부에서 열리는 바이트의 종류가 표시됩니다.

왼쪽 메일함의 폭은 오른쪽 경계를 드래그하거나 경계에서 좌우 방향키를 눌러 조절합니다. 창이 좁아지면 목록과 읽기 창에 필요한 폭을 확보하고, 충분히 넓어지면 이전 폭을 다시 적용합니다. [WPF GridSplitter](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-resize-columns-with-a-gridsplitter)

## 판독 경로

- WPF는 첨부 탭이 보이는 상태에서 선택이 220ms 유지되면 기존 세션에서 첨부를 읽습니다. 읽은 길이와 SHA-256은 기존 첨부 추출 경로에서 대조합니다. 선택을 바꾸거나 탭을 나가면 이전 요청을 취소합니다. 결과를 붙일 때에도 현재 선택과 요청을 확인합니다.
- TXT·BIN과 지원하는 일반 텍스트 유형은 읽기 전용 문자로 표시합니다. UTF 계열과 CP949를 판독하고, 텍스트로 보기 어려운 데이터는 볼 수 있는 ASCII 문자와 점으로 표시합니다. 텍스트는 처음 32 KiB, ASCII 문자 보기는 처음 8 KiB를 표시합니다. 제어 문자 일부는 코드값으로 표시합니다.
- Rust 프로세스는 네트워크 권한을 부여하지 않은 Windows AppContainer에서 실행합니다. 요청마다 첨부 바이트를 표준 입력으로 받아 형식을 식별합니다. WPF에는 크기를 검증할 수 있는 고정 머리글과 최대 1,600px 변의 32비트 픽셀만 전달합니다. 프로세스 시작에 실패하면 해당 상태를 표시합니다.
- 첨부 입력을 24 MiB로 제한합니다. ZIP 컨테이너의 멤버는 4,096개, 합산 표기 해제 크기는 96 MiB로 제한합니다. AppContainer와 함께 Job Object로 메모리를 448 MiB로 제한합니다. 렌더러 준비와 응답에는 8초 제한을 적용합니다. 문서가 사용한 글꼴은 시스템에 있는 글꼴로 대체될 수 있습니다.

## 참고한 구현

| 선택 | 적용 이유 |
|---|---|
| Canon iW Desktop · Everlaw Quick Review | 목록과 미리보기가 세로 공간을 공유하고, 둘 사이의 경계를 조절하는 방식을 적용했습니다. 별도 큰 화면에서 첨부를 보는 경우에는 선택한 메일과 파일을 유지합니다. [Canon 미리보기](https://oip.manual.canon/USRMA-1974-00-DS-enUS/contents/03_03_023-dt-mng_doc-basic_fol_doc-prev-dis_doc.html) · [Everlaw Quick Review](https://support.everlaw.com/hc/en-us/articles/360051409152-Quick-Review-Documents-and-Results) |
| QuickLook Next | 목록을 옮겨 다닐 때 같은 위치에 결과를 표시하고, 파서 작업을 취소하는 흐름을 참고했습니다. [QuickLook Next](https://github.com/SherlockChiang/QuickLook.Next) |
| Dangerzone | 신뢰할 수 없는 문서를 격리 공간에서 읽고, 호스트로 픽셀만 옮기는 경계를 참고했습니다. Mail Evidence에서는 요청한 첫 페이지만 반환하므로 Windows AppContainer와 Job Object로 구현했습니다. [Dangerzone](https://docs.dangerzone.rocks/latest/explanation/how-dangerzone-works/) · [AppContainer 실행](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer) |
| Hayro 0.9.0 · rhwp 0.9.1 · rdocx 0.17.0 · image 0.25.10 | PDF, HWP·HWPX, DOCX, JPG·PNG를 각각 렌더링합니다. rhwp가 그린 SVG는 resvg 0.50.0을 같은 격리 프로세스 안에서 호출해 픽셀로 바꿉니다. [Hayro](https://docs.rs/hayro/0.9.0/hayro/) · [rhwp](https://docs.rs/rhwp/0.9.1/rhwp/) · [rdocx](https://docs.rs/rdocx/0.17.0/rdocx/) · [image](https://docs.rs/image/0.25.10/image/) |

## 빌드

Windows x64에서 Mail-Viewer 폴더를 기준으로 실행합니다. 미리보기 워커를 먼저 빌드하면 WPF가 실행 파일을 리소스로 포함합니다. 배포 스크립트는 워커를 Brotli로 압축해 넣습니다. 앱은 미리보기가 필요할 때에만 압축을 풀어 격리 프로세스를 시작합니다.

~~~powershell
$env:RUSTFLAGS = '-C target-feature=+crt-static'
cargo build --manifest-path preview/Cargo.toml --release --locked
pwsh -NoProfile -File tools/publish-mail-evidence.ps1 -OutputDirectory artifacts/mail-evidence-optimized-1.5.6-win-x64 -Variant Both
~~~

`compact`는 설치된 .NET 10 Desktop Runtime(x64)을 사용합니다. `standalone`에는 해당 런타임이 포함됩니다. [Microsoft .NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## 배포 크기 검수

| Windows x64 빌드 | EXE 크기 |
|---|---:|
| 기존 런타임 포함판 | 92,237,015바이트 |
| 비교용 빌드 (미리보기 워커 제외) | 66,502,893바이트 |
| 경량판 | 19,749,424바이트 |
| 런타임 포함판 | 81,918,161바이트 |

Rust의 크기 우선 최적화, Thin LTO, 단일 코드 생성 단위로 워커를 60,630,016바이트에서 46,441,984바이트로 줄였습니다. 경량판은 설치된 .NET 10 Desktop Runtime을 사용하며, 런타임 포함판은 .NET 단일 파일 압축도 적용합니다. [Cargo 릴리스 프로필](https://doc.rust-lang.org/cargo/reference/profiles.html) · [.NET 단일 파일 배포](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

같은 JPG·PNG·PDF·DOCX·HWP·HWPX와 지원하지 않거나 잘린 파일 등 첨부 9개를 기존 워커와 새 워커에서 번갈아 9회 읽었습니다. 각 파일의 판독 상태와 렌더링 응답 전체가 바이트 단위로 일치했습니다. HWPX 표본은 워커 실행을 포함한 중앙값이 164ms에서 240ms로 늘었고, 나머지 표본의 차이는 9ms 이내였습니다.

두 배포본을 실제 Windows 창에서 열어 시작 모달, EML 열기, PDF 미리보기와 압축 해제한 워커의 SHA-256을 확인했습니다. MailViewer.Verification 27개, WPF FlowSmoke 8개 및 NTFS-Tracer 린트가 통과했습니다.

## 검수

- 실제 WPF 창을 별도 Windows 데스크톱에서 실행했습니다. 175% 배율에서 밝고 어두운 테마, 한국어와 영어를 확인했습니다. 일반 창에서 분할 경계의 키보드 조절, 배율 전환, 크게 보기와 `Esc` 복귀를 검증했습니다. 하단에 접힌 파일 정보 머리글을 둔 730×560 DIP 창에서 JPG 미리보기는 275×158 DIP였으며, 파일 정보를 펼칠 수 있었습니다.
- 후속 검사에서는 첨부 80개의 마지막 행 복귀와 원본 바이트 왕복 뒤 초점을 확인했습니다. 950 DIP 영문 창에서 파일 선택 상자 폭은 84 DIP에서 약 414 DIP로 늘었습니다. TXT의 UTF-8·CP949, 빈 TXT, BIN 문자 보기, 단일·복수 첨부의 본문 아이콘을 검증했습니다.
- 검증용 EML에 넣은 PDF는 작업공간에 있던 MBox Viewer 공개 안내서입니다. HWP·HWPX는 rhwp 공개 표본이고, DOCX·JPG·PNG 및 잘린 PNG와 다른 형식의 파일은 검증용으로 만들었습니다. 연속으로 행을 이동했을 때 마지막 선택만 표시되며, 원본 바이트 탭을 왕복한 뒤에도 자동 미리보기가 동작했습니다. 검수 전후 EML의 SHA-256도 일치했습니다. [HWP 표본](https://github.com/edwardkim/rhwp/blob/981ce66b4183b13c1a3b5907e04a8969c8c42034/samples/atop-equation-01.hwp) · [HWPX 표본](https://github.com/edwardkim/rhwp/blob/981ce66b4183b13c1a3b5907e04a8969c8c42034/samples/hwpx/hwpx-h-01.hwpx)
- 별도 통제 프로그램으로 AppContainer 토큰을 확인하고 사용자 임시 파일 읽기와 로컬 TCP 연결이 거부되는 것을 확인했습니다. MailViewer.Verification 27개, 기존 메일 FlowSmoke, 첨부 미리보기 FlowSmoke, Rust fmt·Clippy와 NTFS-Tracer 린트를 실행했습니다.
