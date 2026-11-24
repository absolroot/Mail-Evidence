# PST 판독과 내보내기 검증

## 공개 표본과 독립 기준값

제품 파서와 분리된 Mbox Viewer 배포본의 `pst-extractor` 1.12.0으로 PST의 폴더와 메시지를 열었다. 전송 헤더와 첨부는 텍스트나 EML로 변환하기 전에 MAPI 속성 바이트를 직접 추출했다. 검색 결과만 나타내는 가상 검색 폴더는 일반 폴더 순회에서 제외했다. [바이트·NID·SHA-256 기준값](../tests/fixtures/pst-oracle.json)

| 표본 | 독립 판독 결과 |
|---|---|
| Apache Tika `testPST.pst` | Unicode v23, 271,360바이트, SHA-256 `acad1e91d58d51494b459d986a1550b352031f0088a90f8e68d5ef449f3e9f5e`. `Début du fichier de données Outlook` 폴더 NID 32802에 메일 6건. 각 `0x007D001F` 속성에서 `Received`가 모두 50개이며, 첨부는 0개다. [표본 경로와 다운로드](../tests/fixtures/README.md) |
| libpff `outlook.pst` | Unicode v23, 271,360바이트, SHA-256 `df01707f76d0e24ab913cf1ffeffa6eaf9c1d590e02102c7ed26bb3ac51d4e24`. `Inbox` NID 33090과 `Recovered_Group2` NID 33410에 제목이 같은 메일이 각각 하나 있으며 두 메일의 첨부는 각각 11개다. `Inbox`의 `1.jpg`는 메시지 NID 2097188의 첨부 NID 32805, 속성 `0x37010102`, 1,969바이트, SHA-256 `0c7da5374fe7e65b84521e111a584c5508eb40e1f8f044e4ff9416740d601311`이다. [libyal/testdata 원본과 고정 커밋](https://github.com/libyal/testdata/blob/c3631ff17ce4ae6798ed9abd065c6a67ad626bce/pst/outlook.pst) |

추가 검증 표본은 libyal/testdata가 libpff 시험용으로 공개한 파일이며, 원본 바이트를 유지하고 로컬 파일명에 출처를 덧붙였다. [libpff의 시험 자료 설정](https://github.com/libyal/libpff/blob/main/libpff.ini)

## 실행

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release -warnaserror
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release --no-build -- --large
```

콘솔에는 폴더·원본 전송 헤더, 동일 이름 첨부의 소속 범위, 검색 CSV와 원본 속성 내보내기, 손상 파일 격리 시험을 추가했다. [PST 콘솔 검증 코드](../tests/MailViewer.Verification/PstChecks.cs)

## 시험 결과

2026년 11월 23일 Release 재빌드는 경고 0개, 오류 0개였고 콘솔 실행은 **19건 모두 통과**했다. PST 신규 시험 4건과 기존 EML·MBOX·MSG 회귀 시험 15건을 실행했다.

| 시험 | 확인한 결과 |
|---|---|
| Tika 폴더와 전송 헤더 | 폴더 계층과 메시지 NID를 확인했다. 해당 폴더에서 제목 `Feature Generators` 2건과 본문 `java.lang.VerifyError` 1건을 찾았다. 6개 메시지의 `0x007D001F` 전체 바이트와 SHA-256, 개별 헤더 행의 속성 내 위치와 바이트를 대조했다. 같은 이름의 `Received` 50개는 원본 순서대로 남았다. |
| libpff 폴더 범위와 첨부 | 같은 제목과 첨부명이 있어도 `Inbox`와 `Recovered_Group2` 검색 결과는 각 폴더의 메시지 1건으로 제한됐다. 상위 폴더만 검색했을 때 0건이었고 하위 포함을 켜면 2건이었다. 두 PST에서 같은 숫자 NID는 자료 식별자로 구분했다. 첨부 22개의 바이트·길이·SHA-256, 각 첨부의 512바이트 경계를 가로지르는 속성 내 범위를 독립 기준값과 대조했다. |
| 검색 결과와 내보내기 | 검색 CSV에는 선택 폴더에서 찾은 레코드 1건만 기록했다. PST 근거 묶음의 헤더 `.bin`이 원본 속성 바이트와 같았다. 헤더가 없던 메일은 메타데이터만 저장했다. 첨부 `1.jpg`는 저장본의 전체 바이트와 SHA-256이 원본 속성과 같았다. 매니페스트에서 폴더·메시지·첨부 NID와 MAPI 태그를 확인했고, 같은 크기로 변조한 저장본은 파일 검증에서 실패했다. |
| 손상 격리와 읽기 전용 | 1,024바이트로 자른 파일을 정상 원본과 함께 열었다. 손상은 별도로 보고했고 정상 원본 메일 6건이 유지됐다. 세션이 열려 있는 동안 원본에 대한 Windows 쓰기 핸들이 거부됐으며 시험 전후 두 PST의 SHA-256은 동일했다. |

## 확인 범위

두 공개 PST는 Unicode v23 정상 메일 표본이다. 전송 헤더와 파일 첨부는 MAPI 속성의 논리 위치로 기록했다. ANSI·다른 Unicode 변형, 삭제 복구, 중첩 메시지 첨부 추출, 대형 실제 PST의 처리량은 별도 검증이 필요하다. 4 GiB 오프셋은 MBOX 회귀 시험에서 확인했다. 화면의 폴더와 검색 상태 복귀 절차는 PST 사용자 흐름 검토에 정리했다.
