\## Tóm tắt --- Module Gamification Milingo

\-\--

\### Bối cảnh project - \*\*Frontend:\*\* Flutter + Riverpod, cấu trúc
feature-first - \*\*Backend:\*\* ASP.NET Core + Firestore (không có
SQL), auth qua Firebase JWT - \*\*Vấn đề ban đầu:\*\* Toàn bộ stats
(coins, streak, points) hardcode trong UI

\-\--

\### Backend --- 4 file đã thêm/sửa

\*\*\`Models/UserStatsModels.cs\`\*\* --- tạo mới -
\`UserStatsResponse\` (coins, currentStreak, totalPoints,
lastStudyDate) - \`RecordStudyRequest\` (placeholder, dễ mở rộng)

\*\*\`Services/IFirestoreService.cs\`\*\* --- thêm 2 method -
\`GetUserStatsAsync(userId)\` --- đọc stats từ Firestore -
\`RecordFlashcardStudyAsync(userId)\` --- cập nhật streak theo logic:
hôm qua học → +1, bỏ ngày → reset 1, hôm nay rồi → idempotent

\*\*\`Services/FirestoreService.cs\`\*\* --- implement 2 method trên -
Dùng Firestore Transaction để tránh race condition khi cập nhật streak -
Fields Firestore: \`coins\`, \`current_streak\`, \`total_points\`,
\`last_study_date\`

\*\*\`Controller/UserController.cs\`\*\* --- thêm 2 endpoint - \`GET
/api/v1/users/stats\` --- trả về stats hiện tại - \`POST
/api/v1/users/record-study\` --- ghi nhận học flashcard hôm nay, trả về
stats mới

\-\--

\### Frontend --- 5 file đã thêm/sửa

\*\*\`lib/core/network/milingo_models.dart\`\*\* --- thêm class ở cuối
file (top-level, ngoài mọi class khác) - \`UserStatsResponse\` với
\`fromJson\`, 4 fields: coins, currentStreak, totalPoints, lastStudyDate

\*\*\`lib/core/network/milingo_api_service.dart\`\*\* --- thêm 2
method - \`getUserStats()\` → \`GET /api/v1/users/stats\` -
\`recordFlashcardStudy()\` → \`POST /api/v1/users/record-study\`

\*\*\`lib/features/gamification/providers/user_stats_provider.dart\`\*\*
--- tạo mới - \`UserStatsNotifier extends
AsyncNotifier\<UserStatsResponse\>\` - \`build()\` → fetch từ backend
khi khởi tạo - \`recordStudy()\` → gọi \`POST record-study\`, cập nhật
state - \`addCoinsOptimistic(amount)\` → cộng coins vào state ngay sau
snap (không cần gọi API lại) - \`refresh()\` → force fetch lại từ
backend - \`userStatsProvider\` --- AsyncNotifierProvider -
\`userStatsValueProvider\` --- sync shim, trả về giá trị mặc định 0 khi
đang load

\*\*\`lib/features/snap_and_learn/providers/snap_provider.dart\`\*\* ---
sửa 3 điểm - Thêm \`Ref \_ref\` vào constructor \`SnapController\` -
Thêm import \`user_stats_provider.dart\` - Trong \`\_analyzeFile\`: sau
khi snap thành công gọi
\`\_ref.read(userStatsProvider.notifier).addCoinsOptimistic(snapResponse.coinsAwarded)\`

\*\*\`lib/features/flashcards/screens/deck_screen.dart\`\*\* --- sửa 3
điểm - Đổi \`StatefulWidget\` → \`ConsumerStatefulWidget\` - Đổi
\`State\` → \`ConsumerState\` - Thêm \`initState\` gọi
\`ref.read(userStatsProvider.notifier).recordStudy()\` qua
\`addPostFrameCallback\`

\-\--

\### Còn lại chưa làm (session tiếp theo)

\| Việc \| File \| \|\-\-\-\-\--\|\-\-\-\-\--\| \| Kết nối stats thật
vào header \| \`simple_home_screen.dart\` \| \| Kết nối tên user + stats
vào profile \| \`profile_screen.dart\` \| \| Kiểm tra session khi mở app
\| \`splash_screen.dart\` \| \| Auth guard \| \`app_router.dart\` \| \|
Kết nối UI flashcards với provider \| \`flashcards_screen.dart\`,
\`deck_screen.dart\` (data thật) \| \| Thêm package \| \`uuid: \^4.4.0\`
vào \`pubspec.yaml\` \|
