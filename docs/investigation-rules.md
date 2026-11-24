# 이상 징후 규칙과 검수

Windows 앱은 C# 코어의 [InvestigationRules](../src/MailViewer.Core/InvestigationRules) 모듈에서 이상 징후를 판정합니다. 모듈은 EML·MBOX·MSG·PST·OST 판독기가 남긴 같은 메일 모델을 받습니다.

| 규칙 | 담당 코드 |
|---|---|
| 단일 헤더 중복, 발신·회신 도메인 차이, 경유 시각 역전 | [HeaderRules.cs](../src/MailViewer.Core/InvestigationRules/HeaderRules.cs) |
| HTML에 표시된 URL과 링크 목적지의 도메인 차이 | [LinkRules.cs](../src/MailViewer.Core/InvestigationRules/LinkRules.cs) |
| 이름 위장, 문서·이미지 이름을 가진 PE, 첨부 시작·끝 구조 | [AttachmentRules.cs](../src/MailViewer.Core/InvestigationRules/AttachmentRules.cs), [AttachmentSignatures.cs](../src/MailViewer.Core/AttachmentSignatures.cs) |
| 대화 내 회신 주소 변경 | [ReplyHistoryRule.cs](../src/MailViewer.Core/InvestigationRules/ReplyHistoryRule.cs) |
| 인증·주소·참조 관측값과 정확 조회 | [InvestigationObservations.cs](../src/MailViewer.Core/InvestigationRules/InvestigationObservations.cs) |

규칙을 추가할 때는 `InvestigationClue`에 새 비트를 추가하고 이전 값을 보존합니다. 적중 조건에서 `BeginFinding`을 호출하고, 확인할 수 없었던 범위는 `Unavailable`로 표시합니다. 같은 사건의 근거는 같은 번호를 사용합니다. 디코딩 바이트와 원본 컨테이너의 위치는 각각의 위치 기준을 유지합니다. 새 규칙은 [평가 순서](../src/MailViewer.Core/InvestigationRules/InvestigationRules.cs)와 앱의 [필터·상세 표시](../src/MailViewer.App/MainWindow.Investigation.cs)에 연결합니다.

## 세 가지 검토

1. **규칙을 추가했을 때 출력 차이가 생기는가.** 검색용 판정과 상세 근거가 같은 조건을 통과합니다. 요약과 상세의 순서·적중·미확인 건수, 정확 조회와 관측값 일치를 회귀 검증에 추가했습니다. 규칙에 식별자를 부여하고 등록하는 구성은 [Rspamd의 공개 규칙](https://github.com/rspamd/rspamd/blob/master/rules/misc.lua)을 참고했습니다.
2. **메일함 전체를 세는 동안 메모리가 증가하는가.** 메일마다 적중·미확인 플래그만 캐시합니다. 상세 근거와 회신 비교는 최근 조회 한 건씩 재사용합니다. 참조 헤더가 없는 메일함은 대화 색인 생성을 생략하고, 자료를 추가한 세션에서는 참조가 있는 메일을 다시 비교합니다. 캐시에 넣기 전에 취소를 확인하고, 상세를 보관할 때는 완성된 결과를 교체합니다. [ConcurrentDictionary의 갱신 동작](https://learn.microsoft.com/en-us/dotnet/standard/collections/thread-safe/how-to-add-and-remove-items)
3. **오래 스크롤하거나 검색 결과가 바뀔 때 이미지가 쌓이는가.** 썸네일 캐시에서 항목을 빼면 그 이미지를 갖고 있는 행의 참조도 지웁니다. 대기 중 같은 이미지가 캐시에 들어오면 해당 이미지를 재사용합니다. 이미지 태그가 없는 HTML에서 목록용 이미지 파서를 호출하지 않습니다. 기존 이미지 작업을 확인하기 위해 날짜별 그룹, 첨부 카드와 본문 이미지, 핀, 북마크 저장·내보내기를 실제 WPF 창에서 점검했습니다.

## 같은 자료로 비교한 성능

Windows x64에서 같은 합성 MBOX를 변경 전후로 번갈아 열었습니다. 각각 새 프로세스에서 5회 실행하고 중앙값을 비교했습니다. 8,000통 중 대화 자료에서는 6,000통에 참조를 넣었습니다. 관리 메모리는 첫 전체 검색 뒤와 이상 징후 건수 계산 뒤에 각각 GC를 실행해 비교했습니다.

| 코어 단계 | 참조 없는 메일 8,000통 | 참조가 있는 메일 6,000통을 포함한 8,000통 |
|---|---:|---:|
| 자료 열기 | 413 → 391 ms | 442 → 428 ms |
| 첫 전체 검색 | 2.92 → 2.51 ms | 3.26 → 3.07 ms |
| 이상 징후 건수 계산 | 203 → 70 ms | 361 → 147 ms |
| 이상 징후 반복 검색 20회 | 139 → 16 ms | 242 → 18 ms |
| 건수 계산 뒤 관리 메모리 | 68.47 → 33.90 MiB | 83.21 → 38.74 MiB |
| 발신 주소 첫 정확 검색 | 4.94 → 11.39 ms | 11.11 → 11.72 ms |
| 메일 100통의 상세 근거 조회 | 0.57 → 5.43 ms | 0.59 → 5.56 ms |

상세를 열 때 근거를 만드는 비용이 조회 단계로 이동했습니다. 이 표의 첫 전체 검색은 코어가 결과를 반환하기까지의 시간입니다. WPF 화면이 그려지는 시간과 프로세스 전체 메모리는 별도로 측정하지 않았습니다.

합성 MBOX 두 종류와 공개 MBOX·합성 EML을 섞은 52통에서 이상 징후 건수, 미확인 건수, 결과 행의 순서와 원본 위치, 상세 근거 전체의 SHA-256이 일치했습니다. 기존 자료 로드·검색·원본 대조·저장 검증과 NTFS Tracer의 검사를 실행했습니다.
