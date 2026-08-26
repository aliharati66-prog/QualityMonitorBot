using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System.IO;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using System.Linq;

class Program
{
    private static readonly string SpreadsheetId = "1xfFaeXRK5Q-sw_KMYSG5xpmCGlhRdMq3Fa46h3zKE2s";
    private static readonly string CredentialsPath = "credentials.json";
    private static SheetsService? sheetsService;
    private static string BotToken = "8840542620:AAFH7qbaeB7JAXK1wCMFYEawbBDPihKJh-0";
    private static TelegramBotClient bot = null!;
    private static ConcurrentDictionary<long, UserState> UserStates = new();

    // لیست Staff (مدرس + ادمین + کارشناس)
    private static HashSet<long> StaffIds = new HashSet<long>()
    {
        107592700,   // علی هراتی‌بندی
        // آیدی بقیه Staff را اینجا اضافه کن
    };

    class ClassInfo
    {
        public long GroupChatId;
        public string ClassCode = "";
        public long TeacherTelegramId;
        public string TeacherName = "";
        public List<StudentInfo> Students = new List<StudentInfo>();
    }

    class StudentInfo
    {
        public long TelegramId;
        public string FullName = "";
    }

    static List<ClassInfo> Classes = new List<ClassInfo>();

    static async Task Main()
    {
        // ---------- کلاس نمونه ----------
        var sampleClass = new ClassInfo
        {
            GroupChatId = -1004349341642,
            ClassCode = "EB-SM-ZZ",
            TeacherTelegramId = 107592700,
            TeacherName = "Ali Haratibandi"
        };
        sampleClass.Students.Add(new StudentInfo { TelegramId = 156246610, FullName = "Maryam" });
        Classes.Add(sampleClass);

        bot = new TelegramBotClient(BotToken);
        var me = await bot.GetMe();
        Console.WriteLine("ربات @" + me.Username + " در حال اجرا است...");

        bot.OnMessage += OnMessage;
        bot.OnUpdate += OnUpdate;
        bot.OnError += (ex, src) => { Console.WriteLine(ex.Message); return Task.CompletedTask; };

        // برای Railway و سرورهای ابری
        await Task.Delay(Timeout.Infinite);
    }

    static async Task OnMessage(Message msg, UpdateType type)
    {
        if (msg.Text == null || msg.From == null) return;

        long chatId = msg.Chat.Id;
        long userId = msg.From.Id;
        string text = msg.Text.Trim();
        string fullName = (msg.From.FirstName ?? "") + (string.IsNullOrEmpty(msg.From.LastName) ? "" : " " + msg.From.LastName);

        Console.WriteLine($"ChatId: {chatId} | UserId: {userId} | From: {fullName} | Text: {text}");

        // === ثبت خودکار زبان‌آموز در گروه ===
        if (msg.Chat.Type == ChatType.Group || msg.Chat.Type == ChatType.Supergroup)
        {
            var currentClass = Classes.FirstOrDefault(c => c.GroupChatId == chatId);
            if (currentClass != null && !StaffIds.Contains(userId))
            {
                if (!currentClass.Students.Any(s => s.TelegramId == userId))
                {
                    currentClass.Students.Add(new StudentInfo
                    {
                        TelegramId = userId,
                        FullName = fullName.Trim()
                    });
                    Console.WriteLine("زبان‌آموز جدید ثبت شد: " + fullName + " (" + userId + ")");
                }
            }
        }

        // مدیریت نظر متنی
        if (UserStates.TryGetValue(chatId, out var state) && state.WaitingForText)
        {
            state.FreeText = text.ToLower() == "خیر" ? null : text;
            state.WaitingForText = false;
            await SaveFeedback(state);
            await bot.SendMessage(chatId, "✅ فیدبک شما ثبت شد. متشکریم!");
            UserStates.TryRemove(chatId, out _);
            return;
        }

        // دستور /start با deep link
        if (text.StartsWith("/start"))
        {
            if (text.Contains("feedback_"))
            {
                string classCode = text.Replace("/start feedback_", "").Trim();
                var currentClass = Classes.FirstOrDefault(c => c.ClassCode == classCode);
                if (currentClass == null)
                {
                    await bot.SendMessage(chatId, "کلاس مورد نظر پیدا نشد.");
                    return;
                }

                if (userId == currentClass.TeacherTelegramId || StaffIds.Contains(userId))
                {
                    // مدرس است
                    await ShowStudentList(chatId, currentClass);
                }
                else if (currentClass.Students.Any(s => s.TelegramId == userId))
                {
                    // زبان‌آموز است
                    if (!UserStates.ContainsKey(chatId))
                        UserStates[chatId] = new UserState();

                    var st = UserStates[chatId];
                    st.Role = "student";
                    st.CurrentStep = 1;
                    st.SelectedClassCode = currentClass.ClassCode;

                    await bot.SendMessage(chatId, "فیدبک شما به صورت ناشناس ثبت می‌شود.\nشروع ارزیابی مدرس:");
                    await SendStudentQuestion(chatId, 1);
                }
                else
                {
                    await bot.SendMessage(chatId, "شما در لیست این کلاس ثبت نشده‌اید. لطفاً یک پیام در گروه کلاس بفرستید.");
                }
            }
            else
            {
                await bot.SendMessage(chatId, "سلام! برای شروع فیدبک، ابتدا داخل گروه کلاس دستور /feedback را بفرستید.");
            }
        }
        else if (text == "/setupbutton")
        {
            if (msg.Chat.Type != ChatType.Group && msg.Chat.Type != ChatType.Supergroup)
            {
                await bot.SendMessage(chatId, "این دستور فقط داخل گروه کلاس قابل استفاده است.");
                return;
            }

            var currentClass = Classes.FirstOrDefault(c => c.GroupChatId == chatId);
            if (currentClass == null)
            {
                await bot.SendMessage(chatId, "این گروه در سیستم ثبت نشده است.");
                return;
            }

            var me = await bot.GetMe();
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithUrl("📝 ثبت فیدبک", $"https://t.me/{me.Username}?start=feedback_{currentClass.ClassCode}")
                }
            });

            await bot.SendMessage(chatId, "برای ثبت فیدبک روی دکمه زیر کلیک کنید:", replyMarkup: keyboard);
        }
        else if (text == "/feedback")
        {
            if (msg.Chat.Type == ChatType.Group || msg.Chat.Type == ChatType.Supergroup)
            {
                var currentClass = Classes.FirstOrDefault(c => c.GroupChatId == chatId);
                if (currentClass == null)
                {
                    await bot.SendMessage(chatId, "این گروه در سیستم ثبت نشده است.");
                    return;
                }

                var me = await bot.GetMe();
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("شروع فیدبک (خصوصی)", $"https://t.me/{me.Username}?start=feedback_{currentClass.ClassCode}")
                    }
                });

                await bot.SendMessage(chatId,
                    "برای جلوگیری از شلوغی گروه، لطفاً روی دکمه زیر کلیک کنید تا فیدبک را در چت خصوصی ادامه دهید:",
                    replyMarkup: keyboard);
            }
            else
            {
                await bot.SendMessage(chatId, "لطفاً ابتدا از داخل گروه کلاس دستور /feedback را بفرستید.");
            }
        }
        else
        {
            // فقط در چت خصوصی جواب بده
            if (msg.Chat.Type == ChatType.Private)
            {
                await bot.SendMessage(chatId, "دستور ناشناخته است. از /feedback استفاده کنید.");
            }
        }
    }

    static async Task OnUpdate(Update update)
    {
        if (update.CallbackQuery == null) return;

        var query = update.CallbackQuery;
        await bot.AnswerCallbackQuery(query.Id);

        long chatId = query.Message!.Chat.Id;
        string data = query.Data ?? "";

        if (!UserStates.ContainsKey(chatId))
            UserStates[chatId] = new UserState();

        var state = UserStates[chatId];

        if (data.StartsWith("select_student_"))
        {
            long studentId = long.Parse(data.Replace("select_student_", ""));
            state.Role = "teacher";
            state.SelectedStudentId = studentId;
            state.CurrentStep = 1;
            await bot.SendMessage(chatId, "شروع ارزیابی زبان‌آموز انتخاب‌شده:");
            await SendTeacherQuestion(chatId, 1);
        }
        else if (data.StartsWith("stu_"))
        {
            var parts = data.Split('_');
            int step = int.Parse(parts[1]);
            state.Answers[step] = parts[2];
            state.CurrentStep = step + 1;

            if (state.CurrentStep <= 7)
                await SendStudentQuestion(chatId, state.CurrentStep);
            else
                await AskFreeText(chatId);
        }
        else if (data.StartsWith("tch_"))
        {
            var parts = data.Split('_');
            int step = int.Parse(parts[1]);
            state.Answers[step] = parts[2];
            state.CurrentStep = step + 1;

            if (state.CurrentStep == 8)
                await SendStrengths(chatId);
            else if (state.CurrentStep <= 11)
                await SendTeacherQuestion(chatId, state.CurrentStep);
            else
                await AskFreeText(chatId);
        }
        else if (data.StartsWith("str_"))
        {
            string code = data.Replace("str_", "");
            if (code == "done")
            {
                state.CurrentStep = 9;
                await SendTeacherQuestion(chatId, 9);
            }
            else
            {
                if (state.Strengths.Contains(code))
                    state.Strengths.Remove(code);
                else
                    state.Strengths.Add(code);
                await SendStrengths(chatId);
            }
        }
    }

    static async Task ShowStudentList(long chatId, ClassInfo classInfo)
    {
        if (classInfo.Students.Count == 0)
        {
            await bot.SendMessage(chatId, "هیچ زبان‌آموزی هنوز ثبت نشده است.\nاز زبان‌آموزان بخواهید یک پیام در گروه بفرستند.");
            return;
        }

        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var student in classInfo.Students)
        {
            buttons.Add(new[] {
                InlineKeyboardButton.WithCallbackData(student.FullName, "select_student_" + student.TelegramId)
            });
        }

        await bot.SendMessage(chatId, "زبان‌آموزی که می‌خواهید فیدبک دهید را انتخاب کنید:",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task SendStudentQuestion(long chatId, int step)
    {
        string text = step switch
        {
            1 => "۱. سطح دانش مدرس\n\n۱. ضعیف\n۲. متوسط رو به پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            2 => "۲. فضای یادگیری کلاس\n\n۱. یکنواخت\n۲. ایستا\n۳. متعادل\n۴. پویا\n۵. بسیار محرک",
            3 => "۳. پیگیری تکالیف\n\n۱. عدم پیگیری\n۲. نامنظم\n۳. استاندارد\n۴. دقیق\n۵. تحلیلی",
            4 => "۴. جو صمیمانه کلاس\n\n۱. بسیار رسمی\n۲. خشک\n۳. معتدل\n۴. گرم\n۵. بسیار صمیمی",
            5 => "۵. استفاده از زبان فارسی\n\n۱. بسیار زیاد\n۲. زیاد\n۳. متعادل\n۴. کم\n۵. بسیار کم",
            6 => "۶. اخلاق و رفتار مدرس\n\n۱. نامناسب\n۲. ضعیف\n۳. متعادل\n۴. خوب\n۵. الگو",
            7 => "۷. انتخاب مدرس برای ترم بعد\n\n۱. قطعاً خیر\n۲. احتمالاً خیر\n۳. نظری ندارم\n۴. احتمالاً بله\n۵. قطعاً بله",
            _ => ""
        };

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("۱", $"stu_{step}_1"),
                InlineKeyboardButton.WithCallbackData("۲", $"stu_{step}_2"),
                InlineKeyboardButton.WithCallbackData("۳", $"stu_{step}_3"),
                InlineKeyboardButton.WithCallbackData("۴", $"stu_{step}_4"),
                InlineKeyboardButton.WithCallbackData("۵", $"stu_{step}_5")
            }
        });

        await bot.SendMessage(chatId, text, replyMarkup: keyboard);
    }

    static async Task SendTeacherQuestion(long chatId, int step)
    {
        string text = step switch
        {
            1 => "۱. پیشرفت کلی\n\n۱. عدم پیشرفت\n۲. کند\n۳. در حد انتظار\n۴. سریع‌تر\n۵. چشمگیر",
            2 => "۲. Speaking\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            3 => "۳. Listening\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            4 => "۴. Reading\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            5 => "۵. Writing\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            6 => "۶. Vocabulary\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            7 => "۷. Grammar\n\n۱. ضعیف\n۲. متوسط پایین\n۳. قابل قبول\n۴. خوب\n۵. عالی",
            9 => "۹. Area for Improvement\n\n۱. نیاز فوری\n۲. نقاط ضعف مشخص\n۳. نیاز به تمرین\n۴. ضعف جزئی\n۵. ضعف عمده ندارد",
            10 => "۱۰. Participation & Attitude\n\n۱. عدم علاقه\n۲. منفعل\n۳. معمول\n۴. مشتاق\n۵. بسیار مشتاق",
            11 => "۱۱. Review & Assessment\n\n۱. عدم آمادگی\n۲. ناقص\n۳. متوسط\n۴. کامل\n۵. فراتر از انتظار",
            _ => ""
        };

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("۱", $"tch_{step}_1"),
                InlineKeyboardButton.WithCallbackData("۲", $"tch_{step}_2"),
                InlineKeyboardButton.WithCallbackData("۳", $"tch_{step}_3"),
                InlineKeyboardButton.WithCallbackData("۴", $"tch_{step}_4"),
                InlineKeyboardButton.WithCallbackData("۵", $"tch_{step}_5")
            }
        });

        await bot.SendMessage(chatId, text, replyMarkup: keyboard);
    }

    static async Task SendStrengths(long chatId)
    {
        var state = UserStates[chatId];
        var buttons = new List<InlineKeyboardButton[]>();

        string[] codes = { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9" };
        string[] labels = { "مشارکت بالا", "روانی کلام", "دایره لغات بالا", "درک مطلب بالا",
                            "دقت گرامری", "شنیداری خوب", "تلاش و پشتکار", "همکاری گروهی", "خلاقیت" };

        for (int i = 0; i < codes.Length; i++)
        {
            string label = state.Strengths.Contains(codes[i]) ? "✅ " + labels[i] : labels[i];
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(label, "str_" + codes[i]) });
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ تمام", "str_done") });

        await bot.SendMessage(chatId, "۸. نقاط قوت (چند مورد انتخاب کنید):",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task AskFreeText(long chatId)
    {
        UserStates[chatId].WaitingForText = true;
        await bot.SendMessage(chatId, "نظر متنی دارید؟ بنویسید یا کلمه «خیر» را بفرستید.");
    }

    static async Task SaveFeedback(UserState state)
    {
        try
        {
            // اگر سرویس هنوز ساخته نشده، آن را بساز
            if (sheetsService == null)
            {
                GoogleCredential credential;
                using (var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read))
                {
                    credential = GoogleCredential.FromStream(stream)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);
                }

                sheetsService = new SheetsService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "QualityMonitorBot"
                });
            }

            var values = new List<object>();
            string range;

            if (state.Role == "student")
            {
                // فیدبک زبان‌آموز به مدرس
                range = "Teacher_Feedback!A:M";

                double average = 0;
                int count = 0;
                for (int i = 1; i <= 7; i++)
                {
                    if (state.Answers.ContainsKey(i) && double.TryParse(state.Answers[i], out double score))
                    {
                        average += score;
                        count++;
                    }
                }
                if (count > 0) average = Math.Round(average / count, 2);

                values.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                values.Add(state.SelectedClassCode);
                values.Add(""); // آیدی مدرس (فعلاً خالی)
                values.Add(""); // نام مدرس
                values.Add(state.Answers.GetValueOrDefault(1, ""));
                values.Add(state.Answers.GetValueOrDefault(2, ""));
                values.Add(state.Answers.GetValueOrDefault(3, ""));
                values.Add(state.Answers.GetValueOrDefault(4, ""));
                values.Add(state.Answers.GetValueOrDefault(5, ""));
                values.Add(state.Answers.GetValueOrDefault(6, ""));
                values.Add(state.Answers.GetValueOrDefault(7, ""));
                values.Add(average);
                values.Add(state.FreeText ?? "");
            }
            else // teacher
            {
                // فیدبک مدرس به زبان‌آموز
                range = "Student_Feedback!A:P";

                values.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                values.Add(state.SelectedClassCode);
                values.Add(state.SelectedStudentId.ToString());
                values.Add(""); // نام زبان‌آموز (فعلاً خالی)
                values.Add(state.Answers.GetValueOrDefault(1, ""));
                values.Add(state.Answers.GetValueOrDefault(2, ""));
                values.Add(state.Answers.GetValueOrDefault(3, ""));
                values.Add(state.Answers.GetValueOrDefault(4, ""));
                values.Add(state.Answers.GetValueOrDefault(5, ""));
                values.Add(state.Answers.GetValueOrDefault(6, ""));
                values.Add(state.Answers.GetValueOrDefault(7, ""));
                values.Add(string.Join(",", state.Strengths));
                values.Add(state.Answers.GetValueOrDefault(9, ""));
                values.Add(state.Answers.GetValueOrDefault(10, ""));
                values.Add(state.Answers.GetValueOrDefault(11, ""));
                values.Add(state.FreeText ?? "");
            }

            var valueRange = new ValueRange { Values = new List<IList<object>> { values } };

            var request = sheetsService.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

            await request.ExecuteAsync();
            Console.WriteLine("فیدبک با موفقیت در گوگل شیت ذخیره شد.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("خطا در ذخیره گوگل شیت: " + ex.Message);
        }
    }

    class UserState
    {
        public string Role = "";
        public int CurrentStep = 0;
        public Dictionary<int, string> Answers = new();
        public HashSet<string> Strengths = new();
        public bool WaitingForText = false;
        public string? FreeText = null;
        public string SelectedClassCode = "";
        public long SelectedStudentId = 0;
    }
}
