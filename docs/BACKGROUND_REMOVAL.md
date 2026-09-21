# AI 배경 제거

Morupixel은 U²-NetP 모델을 포함해 별도 가입·이미지 업로드·모델 다운로드 없이 로컬에서 배경 제거를 실행합니다. Microsoft ONNX Runtime 1.30.0 CPU 엔진을 사용하며, 결과는 레이어의 수정 가능한 마스크로 적용합니다. 기존 마스크가 있으면 새 마스크와 곱해 보존합니다.

Windows AI 엔진은 [ONNX Runtime의 공식 요구사항](https://onnxruntime.ai/docs/install/#requirements)에 따라 Visual C++ 런타임이 필요합니다. ZIP에는 ONNX와 .NET, 모델을 포함하지만 시스템 Visual C++ 런타임은 포함하지 않습니다. AI 엔진 로드 오류가 나면 [Microsoft의 최신 x64 재배포 패키지](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)를 설치한 뒤 앱을 다시 실행하세요. 이미 설치된 이 개발 PC에서는 실제 추론 검증을 통과했으며 새 Windows 설치 환경은 별도 검증이 필요합니다.

사진 크기에 관계없이 모델 입력은 320×320 RGB입니다. Lanczos 크기 변환, 이미지 최댓값 정규화와 ImageNet 평균/표준편차를 사용하고, 첫 번째 출력 마스크를 최솟값/최댓값으로 정규화해 원래 해상도로 복원합니다. 원본 픽셀과 투명도를 직접 지우지 않습니다.

이 모델은 경량 살리언시 검출 모델입니다. 가는 머리카락, 털, 반투명 물체, 배경과 비슷한 색상의 피사체에서는 윤곽이 부정확할 수 있습니다. 마스크 브러시로 결과를 수정할 수 있습니다. 원작 macOS의 Apple Vision과 동일한 모델 또는 동등한 분할 품질을 주장하지 않습니다.

배포 ZIP에서 `models/u2netp.onnx`를 지우거나 실행 파일만 복사하면 모델을 찾을 수 없습니다. 전체 ZIP을 다시 압축 해제하세요. 별도 모델을 선택하는 경우 지원 계약은 float32 입력 `[1,3,320,320]`과 첫 출력 `[1,1,320,320]`인 U²-Net/U²-NetP입니다. 다른 ONNX 모델은 같은 파일 확장자를 가져도 지원하지 않을 수 있습니다.

모델 출처·라이선스·파일 해시는 [모델 고지](../models/README.md), 원본 Apache-2.0 전문은 [U2NET-LICENSE.txt](../models/U2NET-LICENSE.txt)에 있습니다. 자동 테스트는 모델 해시, 실제 CPU 추론, 출력 크기, 취소 및 마스크 결합을 검사합니다. 합성 테스트 통과는 사진별 품질 검증과 구분합니다.
