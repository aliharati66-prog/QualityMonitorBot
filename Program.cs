using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using System.Linq;

class Program
{
    private static string BotToken = "8840542620:AAFH7qbaeB7JAXK1wCMFYEawbBDPihKJh-0";
    private static TelegramBotClient bot = null!;
    private static ConcurrentDictionary<long, UserState> UserStates = new();
    // لیست آیدی عددی Staff (مدرس + ادمین + کارشناس)
    private static HashSet<long> StaffIds = new HashSet<long>()
{
    107592700,   // علی هراتی‌بندی (مدرس)
    // اینجا آیدی بقیه Staff را اضافه کن
};
    class ClassInfo
    {
        public long GroupChatId;
        public string ClassCode;
        public long TeacherTelegramId;
        public string TeacherName;
        public List<StudentInfo> Students = new List<StudentInfo>();
    }

    class StudentInfo
    {
        public long TelegramId;
        public string FullName;
    }

    static List<ClassInfo> Classes = new List<ClassInfo>();

    static async Task Main()
    {
        // ---------- کلاس نمونه ----------
        var sampleClass = new ClassInfo();
        sampleClass.GroupChatId = -1004349341642;
        sampleClass.ClassCode = "EB-SM-ZZ";
        sampleClass.TeacherTelegramId = 107592700;
        sampleClass.TeacherName = "Ali Haratibandi";
        sampleClass.Students.Add(new StudentInfo { TelegramId = 156246610, FullName = "Maryam" });
        Classes.Add(sampleClass);

        bot = new TelegramBotClient(BotToken);
        var me = await bot.GetMe();
        Console.WriteLine("ربات @" + me.Username + " در حال اجرا است...");

        bot.OnMessage += OnMessage;
        bot.OnUpdate += OnUpdate;
        bot.OnError += (ex, src) => { Console.WriteLine(ex.Message); return Task.CompletedTask; };

        Console.ReadLine();
    }

    static async Task OnMessage(Message msg, UpdateType type)
    {
        if (msg.Text == null) return;

        long chatId = msg.Chat.Id;
        string text = msg.Text.Trim();

        Console.WriteLine("ChatId: " + chatId + " | UserId: " + msg.From.Id + " | From: " + (msg.From?.FirstName ?? "Unknown") + " | Text: " + text);
        // ثبت خودکار زبان‌آموز در گروه
        if ((msg.Chat.Type == ChatType.Group || msg.Chat.Type == ChatType.Supergroup) && msg.From != null)
        {
            long userId = msg.From.Id;
            string fullName = (msg.From.FirstName ?? "") + (msg.From.LastName != null ? " " + msg.From.LastName : "");

            var currentClass = Classes.FirstOrDefault(c => c.GroupChatId == chatId);

            if (currentClass != null)
            {
                // اگر Staff نباشد
                if (!StaffIds.Contains(userId))
                {
                    // اگر قبلاً ثبت نشده باشد
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
        }
        if (UserStates.TryGetValue(chatId, out var state) && state.WaitingForText)
        {
            state.FreeText = text.ToLower() == "خیر" ? null : text;
            state.WaitingForText = false;

            // ذخیره فیدبک در فایل
            SaveFeedback(state, chatId);

            await bot.SendMessage(chatId, "✅ فیدبک شما ثبت شد. متشکریم!");
            UserStates.TryRemove(chatId, out _);
            return;
        }

        if (text.StartsWith("/start"))
        {
            // بررسی اینکه آیا از طریق دکمه فیدبک آمده یا نه
            if (text.Contains("feedback_"))
            {
                string classCode = text.Replace("/start feedback_", "").Trim();

                var currentClass = Classes.FirstOrDefault(c => c.ClassCode == classCode);

                if (currentClass == null)
                {
                    await bot.SendMessage(chatId, "کلاس مورد نظر پیدا نشد.");
                    return;
                }

                long userId = msg.From.Id;

                if (userId == currentClass.TeacherTelegramId)
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
                    await bot.SendMessage(chatId, "شما در لیست این کلاس ثبت نشده‌اید.");
                }
            }
            else
            {
                await bot.SendMessage(chatId, "سلام! برای شروع فیدبک، ابتدا داخل گروه کلاس دستور /feedback را بفرستید.");
            }
        }
        else if (text == "/setupbutton")
        {
            // فقط در گروه کار کند
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

            var keyboard = new InlineKeyboardMarkup(new[]
            {
        new[]
        {
            InlineKeyboardButton.WithUrl("📝 ثبت فیدبک", "https://t.me/" + (await bot.GetMe()).Username + "?start=feedback_" + currentClass.ClassCode)
        }
    });

            await bot.SendMessage(chatId,
                "برای ثبت فیدبک روی دکمه زیر کلیک کنید:",
                replyMarkup: keyboard);
        }
        else if (text == "/feedback")
        {
            // اگر پیام در گروه باشد
            if (msg.Chat.Type == ChatType.Group || msg.Chat.Type == ChatType.Supergroup)
            {
                var currentClass = Classes.FirstOrDefault(c => c.GroupChatId == chatId);

                if (currentClass == null)
                {
                    await bot.SendMessage(chatId, "این گروه در سیستم ثبت نشده است.");
                    return;
                }

                // دکمه برای رفتن به چت خصوصی
                var keyboard = new InlineKeyboardMarkup(new[]
                {
            new[]
            {
                InlineKeyboardButton.WithUrl("شروع فیدبک (خصوصی)", "https://t.me/" + (await bot.GetMe()).Username + "?start=feedback_" + currentClass.ClassCode)
            }
        });

                await bot.SendMessage(chatId,
                    "برای جلوگیری از شلوغی گروه، لطفاً روی دکمه زیر کلیک کنید تا فیدبک را در چت خصوصی ادامه دهید:",
                    replyMarkup: keyboard);
            }
            else
            {
                // اگر در چت خصوصی باشد
                await bot.SendMessage(chatId, "لطفاً ابتدا از داخل گروه کلاس دستور /feedback را بفرستید.");
            }
        }
        else
        {
            // فقط در چت خصوصی پیام ناشناخته را جواب بده
            // در گروه هیچ جوابی نده
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

        long chatId = query.Message.Chat.Id;
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

            await bot.SendMessage(chatId, "شروع ارزیابی زبان‌آموز انتخاب شده:");
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
            await bot.SendMessage(chatId, "هیچ زبان‌آموزی ثبت نشده است.");
            return;
        }

        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var student in classInfo.Students)
        {
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(student.FullName, "select_student_" + student.TelegramId) });
        }

        await bot.SendMessage(chatId, "زبان‌آموزی که می‌خواهید فیدبک دهید را انتخاب کنید:", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task SendStudentQuestion(long chatId, int step)
    {
        string text = "";
        switch (step)
        {
            case 1: text = "1. سطح دانش مدرس\n\n1. ضعیف\n2. متوسط رو به پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 2: text = "2. فضای یادگیری کلاس\n\n1. یکنواخت\n2. ایستا\n3. متعادل\n4. پویا\n5. بسیار محرک"; break;
            case 3: text = "3. پیگیری تکالیف\n\n1. عدم پیگیری\n2. نامنظم\n3. استاندارد\n4. دقیق\n5. تحلیلی"; break;
            case 4: text = "4. جو صمیمانه کلاس\n\n1. بسیار رسمی\n2. خشک\n3. معتدل\n4. گرم\n5. بسیار صمیمی"; break;
            case 5: text = "5. استفاده از زبان فارسی\n\n1. بسیار زیاد\n2. زیاد\n3. متعادل\n4. کم\n5. بسیار کم"; break;
            case 6: text = "6. اخلاق و رفتار مدرس\n\n1. نامناسب\n2. ضعیف\n3. متعادل\n4. خوب\n5. الگو"; break;
            case 7: text = "7. انتخاب مدرس برای ترم بعد\n\n1. قطعا خیر\n2. احتمالا خیر\n3. نظری ندارم\n4. احتمالا بله\n5. قطعا بله"; break;
        }

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", "stu_" + step + "_1"),
                InlineKeyboardButton.WithCallbackData("2", "stu_" + step + "_2"),
                InlineKeyboardButton.WithCallbackData("3", "stu_" + step + "_3"),
                InlineKeyboardButton.WithCallbackData("4", "stu_" + step + "_4"),
                InlineKeyboardButton.WithCallbackData("5", "stu_" + step + "_5")
            }
        });
        await bot.SendMessage(chatId, text, replyMarkup: keyboard);
    }

    static async Task SendTeacherQuestion(long chatId, int step)
    {
        string text = "";
        switch (step)
        {
            case 1: text = "1. پیشرفت کلی\n\n1. عدم پیشرفت\n2. کند\n3. در حد انتظار\n4. سریع‌تر\n5. چشمگیر"; break;
            case 2: text = "2. Speaking\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 3: text = "3. Listening\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 4: text = "4. Reading\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 5: text = "5. Writing\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 6: text = "6. Vocabulary\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 7: text = "7. Grammar\n\n1. ضعیف\n2. متوسط پایین\n3. قابل قبول\n4. خوب\n5. عالی"; break;
            case 9: text = "9. Area for Improvement\n\n1. نیاز فوری\n2. نقاط ضعف مشخص\n3. نیاز به تمرین\n4. ضعف جزئی\n5. ضعف عمده ندارد"; break;
            case 10: text = "10. Participation & Attitude\n\n1. عدم علاقه\n2. منفعل\n3. معمول\n4. مشتاق\n5. بسیار مشتاق"; break;
            case 11: text = "11. Review & Assessment\n\n1. عدم آمادگی\n2. ناقص\n3. متوسط\n4. کامل\n5. فراتر از انتظار"; break;
        }

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", "tch_" + step + "_1"),
                InlineKeyboardButton.WithCallbackData("2", "tch_" + step + "_2"),
                InlineKeyboardButton.WithCallbackData("3", "tch_" + step + "_3"),
                InlineKeyboardButton.WithCallbackData("4", "tch_" + step + "_4"),
                InlineKeyboardButton.WithCallbackData("5", "tch_" + step + "_5")
            }
        });
        await bot.SendMessage(chatId, text, replyMarkup: keyboard);
    }

    static async Task SendStrengths(long chatId)
    {
        var state = UserStates[chatId];
        var buttons = new List<InlineKeyboardButton[]>();
        string[] codes = { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9" };
        string[] labels = { "مشارکت بالا", "روانی کلام", "دایره لغات بالا", "درک مطلب بالا", "دقت گرامری", "شنیداری خوب", "تلاش و پشتکار", "همکاری گروهی", "خلاقیت" };

        for (int i = 0; i < codes.Length; i++)
        {
            string label = state.Strengths.Contains(codes[i]) ? "✅ " + labels[i] : labels[i];
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(label, "str_" + codes[i]) });
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ تمام", "str_done") });

        await bot.SendMessage(chatId, "8. نقاط قوت (چند مورد انتخاب کنید):", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task AskFreeText(long chatId)
    {
        UserStates[chatId].WaitingForText = true;
        await bot.SendMessage(chatId, "نظر متنی دارید؟ بنویسید یا کلمه خیر را بفرستید.");
    }
    static void SaveFeedback(UserState state, long chatId)
    {
        try
        {
            string fileName = "feedbacks.txt";
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " | ";
            line += "Role: " + state.Role + " | ";
            line += "Class: " + state.SelectedClassCode + " | ";

            if (state.Role == "teacher")
                line += "StudentId: " + state.SelectedStudentId + " | ";

            line += "Answers: ";
            foreach (var ans in state.Answers)
                line += ans.Key + "=" + ans.Value + ", ";

            if (state.Strengths.Count > 0)
                line += " | Strengths: " + string.Join(",", state.Strengths);

            if (!string.IsNullOrEmpty(state.FreeText))
                line += " | FreeText: " + state.FreeText;

            line += Environment.NewLine;

            File.AppendAllText(fileName, line);
            Console.WriteLine("فیدبک ذخیره شد.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("خطا در ذخیره: " + ex.Message);
        }
    }
    class UserState
    {
        public string Role = "";
        public int CurrentStep = 0;
        public Dictionary<int, string> Answers = new();
        public HashSet<string> Strengths = new();
        public bool WaitingForText = false;
        public string FreeText = null;
        public string SelectedClassCode = "";
        public long SelectedStudentId = 0;
    }
}