# 이상 징후 판독 규칙

| 규칙 | 코드 |
| --- | --- |
| 단일 헤더 중복, 발신·회신 도메인 차이, 경유 시각 역전 | [HeaderRules.cs](../src/MailViewer.Core/InvestigationRules/HeaderRules.cs) |
| HTML에 표시된 URL과 실제 링크의 도메인 차이 | [LinkRules.cs](../src/MailViewer.Core/InvestigationRules/LinkRules.cs) |
| 이중 확장자, 방향 제어 문자, 문서·이미지 이름을 가진 PE, 첨부 구조 이상 | [AttachmentRules.cs](../src/MailViewer.Core/InvestigationRules/AttachmentRules.cs) · [형식별 시그니처](attachment-signatures.md) |
| 같은 대화에서 회신 주소가 바뀐 경우 | [ReplyHistoryRule.cs](../src/MailViewer.Core/InvestigationRules/ReplyHistoryRule.cs) |
| 헤더에 기록된 인증 결과와 주소·URL·해시·ID 조회 | [InvestigationObservations.cs](../src/MailViewer.Core/InvestigationRules/InvestigationObservations.cs) |

## 규칙 추가

`InvestigationClue`에 새 비트를 추가하고 기존 값은 유지합니다. 적중은 `BeginFinding`, 판독할 수 없던 범위는 `Unavailable`로 기록합니다. 같은 이상 징후에 속한 근거에는 같은 번호를 사용합니다. 디코딩한 첨부 오프셋과 원본 컨테이너 안의 위치는 각각의 기준을 기록합니다.

새 규칙을 [코어 평가 순서](../src/MailViewer.Core/InvestigationRules/InvestigationRules.cs)와 [앱의 필터·상세 화면](../src/MailViewer.App/MainWindow.Investigation.cs)에 연결합니다. [코어 시험](../tests/MailViewer.Verification/InvestigationChecks.cs)에서 필터 적중, 근거 위치, 확인 불가 건수와 내보내기 범위를 대조합니다. [시연용 EML](../tests/fixtures/anomaly-ux-complete/README.md)에는 정상 메일과 판독이 제한된 메일도 있습니다.
