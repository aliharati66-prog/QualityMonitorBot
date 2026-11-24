using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    // ===================== CONFIG =====================

    // توکن و secret از environment variable خوانده می‌شن (BOT_TOKEN و BOT_SECRET).
    // اگر BOT_TOKEN تنظیم نشده باشد، مقدار پایین استفاده می‌شود.
    private static readonly string BotToken =
        Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "8840542620:AAFH7qbaeB7JAXK1wCMFYEawbBDPihKJh-0";
    private static readonly string BotSecret =
        Environment.GetEnvironmentVariable("Bot_Secret") ?? "f00eea1e-9de8-4319-9f5f-7356d5dd9bef2f3c34a5-9b08-4749-b68d-9838107a215a";

    private static string StoriesCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vSEHHOfOsg-qr8aPpwvaLuaxXElkhJWNDF9Y5EH3QaY0-Ucjb69-eQGdvgvWO0AD22xchblS4-dns86/pub?gid=680748489&single=true&output=csv";
    private static string PracticeQuestionsCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vSEHHOfOsg-qr8aPpwvaLuaxXElkhJWNDF9Y5EH3QaY0-Ucjb69-eQGdvgvWO0AD22xchblS4-dns86/pub?gid=515468971&single=true&output=csv";
    private static string StudentInfoCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vSEHHOfOsg-qr8aPpwvaLuaxXElkhJWNDF9Y5EH3QaY0-Ucjb69-eQGdvgvWO0AD22xchblS4-dns86/pub?gid=1668847184&single=true&output=csv";
    private static string MistakesCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vSEHHOfOsg-qr8aPpwvaLuaxXElkhJWNDF9Y5EH3QaY0-Ucjb69-eQGdvgvWO0AD22xchblS4-dns86/pub?gid=288141773&single=true&output=csv";
    private static string AttendanceCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vSEHHOfOsg-qr8aPpwvaLuaxXElkhJWNDF9Y5EH3QaY0-Ucjb69-eQGdvgvWO0AD22xchblS4-dns86/pub?gid=401144472&single=true&output=csv";
    private static string AppsScriptUrl = "https://script.google.com/macros/s/AKfycbzChhR3aLc99IlBMnOHyEudoNnv6dlCPn2kk_ULt93jOvUt8wan8eHyUrgHj7gYrJxR0g/exec";

    // حداقل درصد نمره برای رفتن به درس بعد
    private const int PassPercent = 60;
    // تعداد درس‌های هر unit
    private const int LessonsPerUnit = 5;
    // تعداد دکمه درس در هر ردیف (هنگام انتخاب درس در /classdone)
    private const int LessonButtonsPerRow = 5;
    // سقف سوال مرور در هر جلسه
    private const int MaxReviewPerSession = 10;
    // سقف سوال اشتباهات در جلسه روزانه
    private const int DailyMistakeCap = 8;
    // سقف کل سوالات جلسه روزانه (حداقل ۱۵ سوال وقتی بانک اجازه بدهد)
    private const int DailyMaxQuestions = 15;
    // سقف سوال هر درس گذشته در مرور جلسه روزانه
    private const int DailyReviewPerLesson = 2;
    // ----- گروه کلاس‌ها: class_code -> chat_id گروه تلگرام -----
    // حالت خودکار: بات را به گروه کلاس اضافه کن. عنوان گروه = class_code
    // و توضیحات گروه = level=101;term=2026-1;teacher_id=<id>;teacher_name=<name>
    // این جدول فقط fallback دستی است؛ مرجع اصلی ClassRegistry است که از گروه پر می‌شود.
    // مقدار ۰ یعنی هنوز تنظیم نشده (تکلیف و خلاصه جلسه فقط تو شیت ذخیره می‌شن).
    private static readonly Dictionary<string, long> ClassGroupChatIds = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    // ----- رجیستری کلاس‌ها (خودکار از گروه) -----
    // کلید: class_code (= عنوان گروه) ، مقدار: chat_id گروه + level/term/مدرس
    private static readonly ConcurrentDictionary<string, ClassRegistration> ClassRegistry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ClassRegistryLock = new();

    // پیام «برو چت خصوصی» تو گروه بعد از چند دقیقه پاک بشه
    private const int GroupNoticeDeleteMinutes = 3;

    // ----- نقش‌های گروه کلاس -----
    // مالک گروه = مدیر آکادمی، ادمین = مدرس، بقیه اعضا = زبان‌آموز.
    // هیچ‌وقت ادمین‌ها و خود بات به‌عنوان زبان‌آموز ثبت نمی‌شن.
    // کش نقش‌ها برای هر chat_id حدود ۱۰ دقیقه معتبره و با /syncgroup هم رفرش می‌شه.
    private const int GroupRolesCacheMinutes = 10;
    private static readonly ConcurrentDictionary<long, GroupRoles> GroupRolesCache = new();
    // زبان‌آموزهای شناسایی‌شده از پیام گروه (کلید: chat_id + ":" + user_id)
    private static readonly ConcurrentDictionary<string, GroupStudent> GroupStudents = new();
    // جلوگیری از لاگ تکراری نقش‌ها (هر کاربر هر ۱۰ دقیقه یک بار)
    private static readonly ConcurrentDictionary<string, GroupStaff> GroupStaffMap = new();
    private static readonly ConcurrentDictionary<string, DateTime> RoleLogAt = new();

    // یادآوری روزانه (به وقت ایران)
    private const int ReminderHour = 18;
    private const int ReminderMinute = 0;
    // اگر بات تو این بازه (دقیقه بعد از ساعت یادآوری) روشن بشه و امروز نفرستاده باشه، می‌فرسته
    private const int ReminderWindowMinutes = 180;
    // روزهایی که یادآوری نمی‌ره
    private static readonly DayOfWeek[] ReminderSkipDays = { DayOfWeek.Friday };
    // تکلیف‌های قدیمی‌تر از این تعداد روز یادآوری نمی‌شن
    private const int HomeworkMaxAgeDays = 7;
    // فایلی که تاریخ آخرین یادآوری رو نگه می‌داره (تا با ری‌استارت بات دوباره نفرسته)
    private const string ReminderStateFile = "last_reminder.txt";
    private const string DailyGrammarStateFile = "last_daily_grammar.txt";
    private static string LastDailyGrammarDate = "";
    private static string LastDailyGrammarSkipLog = "";
    // برای اینکه هشدار «ارسال ناقص گرامر روز» روزی یک بار به ادمین برود
    private static string LastDailyGrammarAlertDate = "";

    // ----- Term_ID -----
    // نمایش/fallback. مرجع واقعی ترم، Class_Terms سرور است و این مقدار جای آن را نمی‌گیرد.
    private const string ActiveTermId = "2026-1";
    // اگر خواستی برای یک کلاس نام ترم صریح بدهی: ClassTermIds["CLASS_CODE"] = "CLASS_CODE 2026-08-21";
    private static readonly Dictionary<string, string> ClassTermIds = new(StringComparer.OrdinalIgnoreCase);
    // حداقل فاصله بین دو recompute پشت‌سرهم برای یک کلاس (ثانیه)
    private const int RecomputeThrottleSeconds = 45;
    private static readonly ConcurrentDictionary<string, DateTime> LastRecomputeAt = new(StringComparer.OrdinalIgnoreCase);

    private static string BotUsername = "";
    private static long BotUserId = 0;
    private static string LastReminderDate = "";

    // ConcurrentDictionary است تا مدرس/کلاس جدید که از توضیحات گروه می‌آید در زمان اجرا اضافه شود.
    // کلاس‌های واقعی از گروه/رجیستری اضافه می‌شوند؛ هیچ کلاس دمویی seed نمی‌شود.
    private static readonly ConcurrentDictionary<long, List<TeacherClass>> Teachers = new();

    // ===================== GLOBALS =====================

    private static TelegramBotClient bot = null!;

    // HttpClient مشترک برای CSV
    private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // HttpClient مشترک برای Apps Script (ریدایرکت دستی)
    private static readonly HttpClientHandler appsHandler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer()
    };
    private static readonly HttpClient appsClient = new HttpClient(appsHandler) { Timeout = TimeSpan.FromSeconds(40) };

    private static ConcurrentDictionary<long, PracticeState> PracticeStates = new();
    private static ConcurrentDictionary<long, ClassDoneState> ClassDoneStates = new();
    private static ConcurrentDictionary<long, HomeworkState> HomeworkStates = new();
    private static ConcurrentDictionary<long, PlacementState> PlacementStates = new();
    private static ConcurrentDictionary<long, TeacherFbState> TeacherFbStates = new();
    private static ConcurrentDictionary<long, StudentFbState> StudentFbStates = new();
    private static ConcurrentDictionary<long, TeacherMistakeState> TeacherMistakeStates = new();
    private static ConcurrentDictionary<long, DailyGrammarState> DailyGrammarStates = new();
    private static ConcurrentDictionary<long, PlacementPreferenceState> PlacementPreferenceStates = new();
    private static ConcurrentDictionary<long, TeacherScheduleState> TeacherScheduleStates = new();
    private static List<PlacementQuestion>? PlacementBankCache = null;
    private static DateTime PlacementBankAt = DateTime.MinValue;

    // کش CSV
    private static readonly ConcurrentDictionary<string, CsvCacheItem> CsvCache = new();

    // چون CSV منتشرشده گوگل چند دقیقه دیر آپدیت می‌شه، این‌ها تا رسیدن شیت نگهداری می‌شن
    private static readonly ConcurrentDictionary<string, string> ProgressOverrides = new();
    private static readonly ConcurrentDictionary<string, DateTime> RecentReview = new();
    // student_id|question_id -> آخرین باری که سوال نمایش داده شد (انتخاب کم‌دیده‌ترین‌ها)
    private static readonly ConcurrentDictionary<string, DateTime> QuestionLastAsked = new();
    // student_id -> "yyyy-MM-dd" تاریخ آخرین جلسه کاملِ تمام‌شده (برای تشخیص FULL/Daily در همان روز)
    private static readonly ConcurrentDictionary<string, string> FullPracticeDone = new();
    // student_id|yyyy-MM-dd -> پیام «تمرین انجام شد» جلسه روزانه در گروه فرستاده شده
    private static readonly ConcurrentDictionary<string, bool> PracticeDoneNotified = new();

    // ===================== TIME (Iran) =====================

    private static readonly TimeZoneInfo IranTz = GetIranTz();

    static TimeZoneInfo GetIranTz()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception ex) { AppLog.Warn(nameof(GetIranTz), ex.Message); }
        }
        return TimeZoneInfo.CreateCustomTimeZone("IRST", TimeSpan.FromHours(3.5), "IRST", "IRST");
    }

    static DateTime NowIran() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IranTz);
    static string TodayStr() => NowIran().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static DateTime? ParseDate(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        string[] fmts =
        {
            "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d",
            "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.Date;
        return null;
    }

    // تبدیل yyyy-MM-dd به تاریخ شمسی با ارقام فارسی (مثل «چهارشنبه ۲۷ آبان ۱۴۰۵»)
    static string ToPersianDate(string yyyyMMdd)
    {
        var parsed = ParseDate(yyyyMMdd);
        if (!parsed.HasValue) return yyyyMMdd;

        string[] dayNames = { "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه" };
        string[] monthNames = { "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند" };

        var pc = new PersianCalendar();
        var d = parsed.Value;
        int day = pc.GetDayOfMonth(d);
        int month = pc.GetMonth(d);
        int year = pc.GetYear(d);
        if (month < 1 || month > 12) return yyyyMMdd;

        return dayNames[(int)d.DayOfWeek] + " " + ToPersianDigits(day) + " " + monthNames[month - 1] + " " + ToPersianDigits(year);
    }

    static string ToPersianDigits(int n)
    {
        var sb = new StringBuilder();
        foreach (char c in n.ToString(CultureInfo.InvariantCulture))
            sb.Append(c >= '0' && c <= '9' ? (char)('۰' + (c - '0')) : c);
        return sb.ToString();
    }

    // ===================== MAIN =====================

    static async Task Main()
    {
        if (BotToken == "TOKEN_HERE")
        {
            Console.WriteLine("توکن بات تنظیم نشده. متغیر محیطی BOT_TOKEN را بساز یا TOKEN_HERE را عوض کن.");
            return;
        }

        bot = new TelegramBotClient(BotToken);
        var me = await bot.GetMe();
        BotUserId = me.Id;
        SeedClassRegistry();
        await LoadClassRegistryFromServer();
        Console.WriteLine("ربات @" + me.Username + " در حال اجرا است...");
        BotUsername = me.Username ?? "";
        AppLog.CleanOld();
        AppLog.Info("Main", "bot started as @" + BotUsername + " | logs: " + AppLog.LogDir);
        Console.WriteLine("پوشه لاگ: " + AppLog.LogDir + " | موجود: " + System.IO.Directory.Exists(AppLog.LogDir));
        try
        {
            if (System.IO.File.Exists(ReminderStateFile))
                LastReminderDate = System.IO.File.ReadAllText(ReminderStateFile).Trim();
            if (System.IO.File.Exists(DailyGrammarStateFile))
                LastDailyGrammarDate = System.IO.File.ReadAllText(DailyGrammarStateFile).Trim();
        }
        catch (Exception ex) { AppLog.Warn(nameof(Main), ex.Message); }
        if (string.IsNullOrEmpty(BotSecret))
            Console.WriteLine("هشدار: BOT_SECRET تنظیم نشده؛ درخواست‌ها بدون secret ارسال می‌شن.");
        if (string.IsNullOrEmpty(BotSecret))
        {
            Console.WriteLine("BOT_SECRET تنظیم نشده. متغیر محیطی BOT_SECRET را بساز.");
            Environment.Exit(1);
        }

        // bot.OnMessage += OnMessage;
        // bot.OnUpdate += OnUpdate;
        bot.OnError += OnError;
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
    {
        UpdateType.Message,
        UpdateType.CallbackQuery,
        UpdateType.MyChatMember,
        UpdateType.MessageReaction
    }
        };

        bot.StartReceiving(
            updateHandler: async (client, update, ct) =>
            {
                if (update.Message != null)
                    await OnMessage(update.Message, update.Type);
                else
                    await OnUpdate(update);
            },
            errorHandler: async (client, ex, ct) =>
            {
                await OnError(ex, HandleErrorSource.PollingError);
            },
            receiverOptions: receiverOptions
        );
        _ = Task.Run(ReminderLoop).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
        _ = Task.Run(DailyGrammarLoop).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
        _ = Task.Run(InterviewLoop).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
        await Task.Delay(-1);
    }

    static Task OnError(Exception ex, HandleErrorSource source)
    {
        AppLog.Error("TelegramOnError", "source=" + source, ex);
        return Task.CompletedTask;
    }

    // ===================== ROUTING =====================

    // ===================== MAIN MENU (دکمه‌های منو) =====================

    private const string BtnPractice = "📚 تمرین";
    private const string BtnHomework = "📝 تکلیف";
    private const string BtnEditHomework = "✏️ ویرایش تکلیف";
    private const string BtnClassDone = "🎓 ثبت جلسه کلاس";
    private const string BtnCancel = "❌ لغو";
    private const string BtnPlacement = "🎯 تعیین سطح";
    private const string BtnTeacherFb = "⭐ فیدبک به مدرس";
    private const string BtnStudentFb = "📋 فیدبک به زبان‌آموز";
    private const string BtnMyResult = "📊 نتیجه ترم";
    private const string BtnClassResults = "📈 نتایج کلاس";
    private const string BtnLogMistakes = "✍️ ثبت اشتباه";
    private const string BtnDailyGrammar = "📘 گرامر روز";
    // «برنامه کلاس» = برنامه هفتگی کلاس از شیت Class_Schedule (نه زمان آزاد مدرس)
    private const string BtnClassSchedule = "🗓️ برنامه هفتگی کلاس";
    private const string BtnTeacherAvailability = "⏰ زمان‌های مدرس";

    // ----- تنظیمات تعیین سطح -----
    private const int PlacementBooks = 6;            // تعداد کتاب‌های Evolve
    private const int EvolveUnitsPerBook = 12;       // تعداد Unit هر کتاب (اگر فرق داره عوض کن)
    private const int PlacementPassPercent = 80;     // حداقل درصد درست برای رفتن به کتاب بعدی (۴ از ۵)
    private const int PlacementRetakeDays = 30;      // هر نفر هر چند روز یک بار می‌تونه آزمون بده
    // آیدی تلگرام ادمین‌هایی که نتیجه تعیین سطح براشون میاد (فعلاً فقط یک نفر، برای تست)
    private static readonly long[] PlacementAdminIds = new long[] { 107592700 };
    // آیدی تلگرام ادمین‌هایی که پیام 🚨 ERROR (سیستم لاگ) براشون میاد. اگر خالی باشه، پیام تلگرام فرستاده نمی‌شه.
    private static readonly long[] AdminIds = new long[] { 107592700 };
    // پوشه لاگ. خالی = کنار فایل اجرایی (معمولاً bin\Debug\netX.X\logs). برای پوشه دلخواه مسیر کامل بنویس، مثلاً @"C:\BotLogs"
    private const string LogDirOverride = "";
    // زمان تقریبی اطلاع‌رسانی جلسه تعیین سطح با مدرس (تو پیام‌های زبان‌آموز نمایش داده می‌شه)
    private const string InterviewWaitText = "ظرف ۱ تا ۲ روز";

    // کیبورد دکمه‌ای: مدرس‌ها یک ردیف اضافه (ثبت جلسه) می‌بینن
    static ReplyKeyboardMarkup BuildMainMenu(bool isTeacher)
    {
        var rows = new List<KeyboardButton[]>
        {
            new[] { new KeyboardButton(BtnPractice), new KeyboardButton(BtnHomework) },
            new[] { new KeyboardButton(BtnEditHomework) },
            new[] { new KeyboardButton(BtnPlacement), new KeyboardButton(BtnMyResult) },
            new[] { new KeyboardButton(BtnTeacherFb) }
        };

        // فقط مدرس‌ها
        if (isTeacher)
        {
            rows.Add(new[] { new KeyboardButton(BtnClassDone), new KeyboardButton(BtnStudentFb) });
            rows.Add(new[] { new KeyboardButton(BtnClassResults), new KeyboardButton(BtnLogMistakes) });
            rows.Add(new[] { new KeyboardButton(BtnDailyGrammar), new KeyboardButton(BtnClassSchedule) });
            rows.Add(new[] { new KeyboardButton(BtnTeacherAvailability) });
        }

        rows.Add(new[] { new KeyboardButton(BtnCancel) });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    // متن دکمه را به دستور معادلش تبدیل می‌کند
    static string MapMenuText(string text)
    {
        switch (text)
        {
            case BtnPractice: return "/practice";
            case BtnHomework: return "/homework";
            case BtnEditHomework: return "/edithomework";
            case BtnClassDone: return "/classdone";
            case BtnCancel: return "/cancel";
            case BtnPlacement: return "/placement";
            case BtnTeacherFb: return "/feedback_teacher";
            case BtnStudentFb: return "/feedback_student";
            case BtnMyResult: return "/myresult";
            case BtnClassResults: return "/classresults";
            case BtnLogMistakes: return "/mistakes";
            case BtnDailyGrammar: return "/dailygrammar";
            case BtnClassSchedule: return "/classschedule";
            case BtnTeacherAvailability: return "/availability";
            default: return text;
        }
    }

    static bool IsCommand(string text, string cmd)
    {
        if (text == cmd || text.StartsWith(cmd + " ")) return true;
        if (text.StartsWith(cmd + "@"))
        {
            // فقط وقتی دستور برای همین بات باشد (/practice@اسم_بات)
            string rest = text.Substring(cmd.Length + 1);
            string name = rest.Split(' ')[0];
            return string.Equals(name, BotUsername, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    static bool IsStaffUser(long userId) => AdminIds.Contains(userId) || Teachers.ContainsKey(userId);

    static bool IsAcademyAdmin(long userId) =>
        AdminIds.Contains(userId) ||
        GroupStaffMap.Values.Any(s => s.UserId == userId && s.Role == "admin");

    static bool HasActiveDialog(long userId) =>
        PlacementStates.ContainsKey(userId) || PracticeStates.ContainsKey(userId) ||
        DailyGrammarStates.ContainsKey(userId) || TeacherFbStates.ContainsKey(userId) ||
        StudentFbStates.ContainsKey(userId) || TeacherMistakeStates.ContainsKey(userId);

    static bool HasBlockingDialogForTermHint(long userId) =>
        HasActiveDialog(userId) || HomeworkStates.ContainsKey(userId) ||
        TeacherScheduleStates.ContainsKey(userId) || PlacementPreferenceStates.ContainsKey(userId);

    private const string HomeworkNotWaitingWarning =
        "تکلیف ثبت نشد. اول دکمه «ارسال تکلیف» را بزن، بعد متن/فایل/ویس را بفرست.";

    static async Task Send(long chatId, string text)
    {
        await bot.SendMessage(chatId, text);
    }

    // تقسیم پیام‌های بلند (حد تلگرام ۴۰۹۶ کاراکتر)
    static async Task SendLong(long chatId, string text)
    {
        const int max = 4000;
        while (text.Length > max)
        {
            int cut = text.LastIndexOf('\n', max - 1, max);
            if (cut < max / 2) cut = max;
            await bot.SendMessage(chatId, text.Substring(0, cut));
            text = text.Substring(cut).TrimStart('\n');
        }
        if (text.Length > 0) await bot.SendMessage(chatId, text);
    }

    static async Task OnMessage(Message message, UpdateType type)
    {
        try
        {
            if (message.From == null) return;
            long userId = message.From.Id;
            long chatId = message.Chat.Id;
            bool isPrivate = message.Chat.Type == ChatType.Private;

            // شماره تلفن (Share contact) در مرحله تعیین سطح
            if (message.Contact != null)
            {
                if (isPrivate && PlacementStates.TryGetValue(userId, out var plc) && plc.Step == "phone")
                    await HandlePlacementContact(message, plc);
                return;
            }

            // پیام غیرمتنی (ویس، عکس، ...)
            if (message.Text == null)
            {
                if (isPrivate && HomeworkStates.TryGetValue(userId, out var hw0) && hw0.WaitingSubmission)
                {
                    if (HasHomeworkMedia(message)) await HandleHomeworkMedia(message, hw0);
                    else await Send(chatId, "متن، ویس، عکس یا فایل بفرست یا /cancel بزن.");
                }
                else if (!isPrivate)
                {
                    // ویس/عکس/فایل هم برای تشخیص زبان‌آموز حساب می‌شه (بی‌صدا)
                    await ClassifyGroupMember(message);
                }
                else if (!IsStaffUser(userId) && !HasActiveDialog(userId))
                {
                    await Send(chatId, HomeworkNotWaitingWarning);
                }
                return;
            }

            // در حال ارسال تکلیف، متن خام می‌مونه: دکمه‌های منو کامند نمی‌شن (فقط دکمه لغو)
            string rawText = message.Text.Trim();
            bool waitingHomework = isPrivate && HomeworkStates.TryGetValue(userId, out var hwWait) && hwWait.WaitingSubmission;
            string text = waitingHomework && rawText != BtnCancel ? rawText : MapMenuText(rawText);

            // پیام تو گروه: فقط به چت خصوصی هدایت می‌کنیم
            if (!isPrivate)
            {
                await HandleGroupCommand(message, text);
                return;
            }

            // /cancel برای همه
            if (IsCommand(text, "/cancel"))
            {
                bool a = ClassDoneStates.TryRemove(userId, out _);
                bool b = HomeworkStates.TryRemove(userId, out _);
                bool c = PracticeStates.TryRemove(userId, out _);
                bool d = PlacementStates.TryRemove(userId, out _);
                bool fb = TeacherFbStates.TryRemove(userId, out _) | StudentFbStates.TryRemove(userId, out _);
                bool tm = TeacherMistakeStates.TryRemove(userId, out _);
                bool dg = DailyGrammarStates.TryRemove(userId, out _);
                bool pp = PlacementPreferenceStates.TryRemove(userId, out _);
                bool ts = TeacherScheduleStates.TryRemove(userId, out _);
                await bot.SendMessage(chatId,
                    (a || b || c || d || fb || tm || dg || pp || ts) ? "لغو شد." : "چیزی برای لغو کردن نبود.",
                    replyMarkup: BuildMainMenu(Teachers.ContainsKey(userId)));
                return;
            }

            // ترتیب اولویت متن: Placement > Homework > Practice > Availability/Schedule > بقیه > StartTerm

            // تعیین سطح: گرفتن اسم و شماره
            if (isPrivate && PlacementStates.TryGetValue(userId, out var pls) &&
                (pls.Step == "name" || pls.Step == "phone" || pls.Step == "prev" || pls.Step == "prevtext") &&
                !text.StartsWith("/"))
            {
                await HandlePlacementText(message, pls, text);
                return;
            }

            // Phase 3: مراحل ترجیحات تعیین سطح و برنامه‌های مدرس
            if (isPrivate && PlacementPreferenceStates.TryGetValue(userId, out var pps) &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandlePlacementPreferenceText(message, pps, text);
                return;
            }

            // زبان‌آموز در حال ارسال تکلیف
            if (isPrivate && HomeworkStates.TryGetValue(userId, out var hw) && hw.WaitingSubmission)
            {
                await HandleHomeworkSubmission(message, hw);
                return;
            }

            // گرامر روز باید قبل از PracticeState بررسی شود؛ وگرنه جواب گرامر
            // می‌تواند بی‌صدا در جلسه تمرین مصرف شود و جلسه معطل بماند.
            if (isPrivate && DailyGrammarStates.TryGetValue(userId, out var dgs) &&
                dgs.WaitingAnswer && (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleDailyGrammarAnswer(message, dgs, text);
                return;
            }

            // زبان‌آموز در حال تایپ جواب سوال مرور
            if (isPrivate && PracticeStates.TryGetValue(userId, out var pst) && pst.AwaitingTyped &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleTypedAnswer(message, pst, text);
                return;
            }

            // اگر سوال DailyPrompt به‌صورت ناخواسته در جلسه تمرین مانده باشد، جوابش
            // ذخیره و جلسه ادامه پیدا کند (نه اینکه تمرین معطل بماند).
            if (isPrivate && PracticeStates.TryGetValue(userId, out var pstDaily) &&
                pstDaily.Index < pstDaily.Questions.Count &&
                IsDailyPromptType(pstDaily.Questions[pstDaily.Index].QuestionType) &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleTypedAnswer(message, pstDaily, text);
                return;
            }

            // برنامه کلاس / زمان‌های مدرس: متن زمان باید همین‌جا مصرف شود
            if (isPrivate && TeacherScheduleStates.TryGetValue(userId, out var tss) &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleTeacherScheduleText(message, tss, text);
                return;
            }

            // مدرس در حال نوشتن اشتباهات (خارج از /classdone)
            if (isPrivate && TeacherMistakeStates.TryGetValue(userId, out var tms) && tms.WaitingText &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleTeacherMistakeText(message, tms, text);
                return;
            }

            // فیدبک: متن اختیاری / نقاط قوت
            if (isPrivate && TeacherFbStates.TryGetValue(userId, out var tfs) && tfs.WaitingText &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleTeacherFbText(message, tfs, text);
                return;
            }
            if (isPrivate && StudentFbStates.TryGetValue(userId, out var sfs) && sfs.WaitingText &&
                (!text.StartsWith("/") || IsCommand(text, "/skip")))
            {
                await HandleStudentFbText(message, sfs, text);
                return;
            }

            // مدرس در حال نوشتن نکته/تکلیف
            if (isPrivate && ClassDoneStates.TryGetValue(userId, out var cd) && cd.WaitingText)
            {
                bool isSkip = IsCommand(text, "/skip");
                bool isRestart = IsCommand(text, "/classdone");
                if (!text.StartsWith("/") || isSkip)
                {
                    await HandleClassDoneText(message, cd);
                    return;
                }
                if (!isRestart)
                {
                    await Send(chatId, "در حال ثبت جلسه هستی. متن را بنویس، /skip بزن، یا برای لغو /cancel.");
                    return;
                }
                // /classdone: جلسه جدید شروع می‌شود (ادامه پایین)
            }

            // StartTerm فقط دکمه‌ای است؛ فقط وقتی هیچ دیالوگ دیگری فعال نیست soft-hint بده.
            if (isPrivate && ClassDoneStates.TryGetValue(userId, out var cdTerm) &&
                !string.IsNullOrEmpty(cdTerm.TermSetupStep) && !text.StartsWith("/") &&
                !HasBlockingDialogForTermHint(userId))
            {
                await HandleTermSetupText(message, cdTerm, text);
                return;
            }

            if (IsCommand(text, "/start"))
            {
                if (isPrivate)
                {
                    await bot.SendMessage(chatId,
                        "سلام 👋\nیکی از گزینه‌ها را انتخاب کن:",
                        replyMarkup: BuildMainMenu(Teachers.ContainsKey(userId)));

                    // لینک پارامتردار از گروه: t.me/بات?start=practice
                    string payload = text.Contains(' ') ? text.Substring(text.IndexOf(' ') + 1).Trim() : "";
                    if (payload == "practice") await StartPractice(message);
                    else if (payload == "homework") await StartHomework(message);
                    else if (payload == "classdone") await StartClassDone(message);
                    else if (payload == "placement") await StartPlacement(message);
                    else if (payload == "feedback_teacher") await StartTeacherFeedback(message);
                    else if (payload == "feedback_student") await StartStudentFeedback(message);
                    else if (payload == "myresult") await StartMyResult(message);
                    else if (payload == "classresults") await StartClassResults(message);
                }
                else
                {
                    await Send(chatId, "سلام 👋\nبرای استفاده از ربات، در چت خصوصی /start بزن.");
                }
                return;
            }

            if (IsCommand(text, "/practice")) { await StartPractice(message); return; }
            if (IsCommand(text, "/homework")) { await StartHomework(message); return; }
            if (IsCommand(text, "/edithomework")) { await StartEditHomework(message); return; }
            if (IsCommand(text, "/classdone")) { await StartClassDone(message); return; }
            if (IsCommand(text, "/placement")) { await StartPlacement(message); return; }
            if (IsCommand(text, "/feedback_teacher")) { await StartTeacherFeedback(message); return; }
            if (IsCommand(text, "/feedback_student")) { await StartStudentFeedback(message); return; }
            if (IsCommand(text, "/myresult")) { await StartMyResult(message); return; }
            if (IsCommand(text, "/classresults")) { await StartClassResults(message); return; }
            if (IsCommand(text, "/mistakes")) { await StartTeacherMistakes(message); return; }
            if (IsCommand(text, "/dailygrammar")) { await StartDailyGrammar(message); return; }
            if (IsCommand(text, "/pendinggrammar")) { await StartPendingGrammar(message); return; }
            if (IsCommand(text, "/dailypromptstatus")) { await StartDailyPromptStatus(message); return; }
            if (IsCommand(text, "/classschedule")) { await StartClassSchedule(message); return; }
            if (IsCommand(text, "/availability")) { await StartTeacherAvailability(message); return; }
            if (IsCommand(text, "/recompute_results")) { await StartRecomputeResults(message, text); return; }

            // تست سیستم لاگ (فقط ادمین‌ها): INFO + WARN + هشدار Apps Script + ERROR
            if (IsCommand(text, "/logtest"))
            {
                if (!AdminIds.Contains(userId)) return;

                AppLog.Info("LogTest", "test INFO from /logtest", userId);
                AppLog.Warn("LogTest", "test WARN from /logtest", userId);

                // action ناشناخته: Apps Script باید خودش یک WARN تو System_Logs بنویسه
                await PostToAppsScript(new { action = "log_test_unknown" });

                // ERROR: فایل + کنسول + شیت System_Logs + پیام 🚨 به AdminIds
                AppLog.Error("LogTest", "test ERROR from /logtest " + DateTime.UtcNow.Ticks,
                    new InvalidOperationException("test exception"), userId);

                await Send(chatId,
                    "✅ تست لاگ انجام شد.\n" +
                    "• کنسول و فایل لاگ: ۴ خط (INFO، WARN، WARN، ERROR)\n" +
                    "• تلگرام: یک پیام 🚨 ERROR (به AdminIds)\n" +
                    "• شیت System_Logs: یک ردیف ERROR از Bot و یک ردیف WARN از AppsScript");
                return;
            }

            if (isPrivate && !text.StartsWith("/") && !IsStaffUser(userId) && !HasActiveDialog(userId))
            {
                await Send(chatId, HomeworkNotWaitingWarning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("OnMessage", "unhandled exception", ex, message.From?.Id);
        }
    }

    static async Task OnUpdate(Update update)
    {
        try
        {
            if (update.MessageReaction != null)
            {
                await HandleHomeworkReaction(update.MessageReaction);
                return;
            }
            // بات به گروه اضافه/حذف شد → ثبت خودکار کلاس از روی عنوان و توضیحات گروه
            if (update.MyChatMember != null)
            {
                await HandleMyChatMember(update.MyChatMember);
                return;
            }

            if (update.CallbackQuery == null) return;
            var cq = update.CallbackQuery;
            if (cq.Data == null) return;

            if (cq.Data.StartsWith("pq|")) { await HandlePracticeCallback(cq); return; }
            if (cq.Data.StartsWith("cd|")) { await HandleClassDoneCallback(cq); return; }
            if (cq.Data.StartsWith("ps|")) { await HandleSelfMarkCallback(cq); return; }
            if (cq.Data.StartsWith("hw|")) { await HandleHomeworkCallback(cq); return; }
            if (cq.Data.StartsWith("tm|")) { await HandleTeacherMistakeCallback(cq); return; }
            if (cq.Data.StartsWith("pl|")) { await HandlePlacementCallback(cq); return; }
            if (cq.Data.StartsWith("dg|")) { await HandleDailyGrammarCallback(cq); return; }
            if (cq.Data.StartsWith("dgr|")) { await HandleDailyGrammarReviewCallback(cq); return; }
            if (cq.Data.StartsWith("pp|")) { await HandlePlacementPreferenceCallback(cq); return; }
            if (cq.Data.StartsWith("cs|")) { await HandleClassScheduleCallback(cq); return; }
            if (cq.Data.StartsWith("ta|")) { await HandleTeacherAvailabilityCallback(cq); return; }
            if (cq.Data.StartsWith("tf|")) { await HandleTeacherFbCallback(cq); return; }
            if (cq.Data.StartsWith("sf|")) { await HandleStudentFbCallback(cq); return; }
            if (cq.Data.StartsWith("cr|")) { await HandleClassResultsCallback(cq); return; }
        }
        catch (Exception ex)
        {
            AppLog.Error("OnUpdate", "unhandled exception", ex, update.CallbackQuery?.From?.Id ?? update.Message?.From?.Id);
        }
    }

    // ===================== GROUP =====================

    // chat_id گروه یک کلاس: اول رجیستری خودکار (از گروه)، بعد جدول دستی ClassGroupChatIds.
    // اگر پیدا نشد، فقط Warn می‌دهد و ۰ برمی‌گرداند (هیچ‌جا کرش نمی‌کنیم).
    static long GetClassGroupId(string? classCode)
    {
        string cc = (classCode ?? "").Trim();
        if (cc.Length == 0) return 0;

        if (ClassRegistry.TryGetValue(cc, out var reg))
        {
            if (!reg.Active)
            {
                AppLog.Warn("ClassRegistry", "class " + cc + " is inactive (active=no in group description)");
                return 0;
            }
            if (reg.GroupChatId != 0) return reg.GroupChatId;
        }

        // fallback: جدول دستی
        if (ClassGroupChatIds.TryGetValue(cc, out var id) && id != 0) return id;

        // fallback: آخرین chat_id دیده‌شده برای زبان‌آموزان همین کلاس
        foreach (var s in GroupStudents.Values)
            if (s.ChatId != 0 && Eq(s.ClassCode, cc)) return s.ChatId;

        AppLog.Warn("ClassRegistry", "group chat id not found for class " + cc);
        return 0;
    }

    // سطح کلاس: اول رجیستری، بعد Teachers (سازگاری با hardcode قبلی)
    static string GetClassLevel(string? classCode)
    {
        string cc = (classCode ?? "").Trim();
        if (cc.Length == 0) return "";

        if (ClassRegistry.TryGetValue(cc, out var reg) && !string.IsNullOrWhiteSpace(reg.Level))
            return reg.Level;

        foreach (var kv in Teachers)
        {
            var tc = kv.Value.FirstOrDefault(c => string.Equals(c.ClassCode, cc, StringComparison.OrdinalIgnoreCase));
            if (tc != null && !string.IsNullOrWhiteSpace(tc.Level)) return tc.Level;
        }
        return "";
    }

    // آینه‌کردن سطح/پیشرفت در Student_Info (بی‌صدا). اگر level خالی باشد،
    // Apps Script خودش از Class_Registry/ترم فعال پر می‌کند.
    static void MirrorStudentLevel(string studentId, string classCode, string level,
        string unit = "", string lesson = "")
    {
        if (string.IsNullOrWhiteSpace(studentId) || string.IsNullOrWhiteSpace(classCode)) return;
        PostInBackground(new
        {
            action = "upsert_student",
            student_id = studentId,
            class_code = classCode,
            full_name = "",
            level = level ?? "",
            unit = unit ?? "",
            lesson = lesson ?? ""
        });
    }

    // اسم نمایشی زبان‌آموز از حافظه گروه (برای ستون‌های Name در لاگ‌ها)
    static string GroupStudentName(string studentId)
    {
        if (string.IsNullOrWhiteSpace(studentId)) return "";
        foreach (var s in GroupStudents.Values)
            if (s.StudentId == studentId && !string.IsNullOrWhiteSpace(s.FullName))
                return s.FullName;
        return "";
    }

    // class_code این chat_id از حافظه: اول رجیستری (که خودش از عنوان گروه پر شده)، بعد جدول دستی
    static string ResolveClassCodeFromMemory(long chatId)
    {
        foreach (var kv in ClassRegistry)
            if (kv.Value.GroupChatId == chatId && !string.IsNullOrWhiteSpace(kv.Key)) return kv.Key;

        foreach (var kv in ClassGroupChatIds)
            if (kv.Value == chatId && !string.IsNullOrWhiteSpace(kv.Key)) return kv.Key;

        return "";
    }

    // نقش‌های گروه: ادمین‌ها (staff) + class_code. نتیجه GroupRolesCacheMinutes دقیقه کش می‌شه.
    // هیچ‌وقت exception بیرون نمی‌ده (فقط AppLog). اگر خواندن ادمین‌ها شکست بخوره Loaded=false می‌مونه
    // تا کسی اشتباهی به‌عنوان زبان‌آموز ثبت نشه.
    static async Task<GroupRoles> GetGroupRoles(long chatId, bool force = false)
    {
        var roles = GroupRolesCache.GetOrAdd(chatId, _ => new GroupRoles());

        bool fresh = (NowIran() - roles.At).TotalMinutes < GroupRolesCacheMinutes;
        if (!force && fresh) return roles;    // کش تازه است (نتیجه موفق یا ناموفق هر دو throttle می‌شن)
        if (roles.Refreshing) return roles;   // یک درخواست هم‌زمان کافیه

        roles.Refreshing = true;
        try
        {
            var admins = await bot.GetChatAdministrators(chatId);
            var ids = new HashSet<long>();
            var owners = new HashSet<long>();
            var teachers = new HashSet<long>();
            foreach (var m in admins)
            {
                if (m.User == null) continue;
                if (m.User.Id == BotUserId) continue;   // خود بات هیچ‌وقت زبان‌آموز نمی‌شه
                ids.Add(m.User.Id);                     // owner + admin ها (custom title مهم نیست)
                if (m.Status == ChatMemberStatus.Creator)
                    owners.Add(m.User.Id);
                else if (m.Status == ChatMemberStatus.Administrator)
                    teachers.Add(m.User.Id);
            }
            roles.AdminIds = ids;
            roles.OwnerIds = owners;
            roles.TeacherIds = teachers;

            string cc = ResolveClassCodeFromMemory(chatId);
            if (cc.Length == 0)
            {
                try
                {
                    var chat = await bot.GetChat(chatId);
                    cc = (chat.Title ?? "").Trim();     // عنوان گروه = class_code
                }
                catch (Exception ex)
                {
                    AppLog.Warn("GroupRoles", "GetChat (title) failed, chat=" + chatId + ": " + ex.Message);
                }
            }
            roles.ClassCode = cc;
            roles.At = NowIran();
            roles.Loaded = true;

            // ثبت نقش همه ادمین‌ها (حتی قبل از اولین پیامشان) تا سه نقش آکادمی شناخته شوند.
            if (cc.Length > 0)
            {
                foreach (var m in admins)
                {
                    if (m.User == null || m.User.Id == BotUserId) continue;
                    string role = m.Status == ChatMemberStatus.Creator ? "admin"
                        : m.Status == ChatMemberStatus.Administrator ? "teacher" : "";
                    if (role.Length == 0) continue;
                    string name = ((m.User.FirstName ?? "") + " " + (m.User.LastName ?? "")).Trim();
                    UpsertGroupStaff(cc, chatId, m.User.Id, name, m.User.Username ?? "", "", role);
                }
            }

            AppLog.Info("GroupRoles", "chat=" + chatId + " class=" + (cc.Length == 0 ? "-" : cc) +
                " staff=" + ids.Count + " owners=" + owners.Count + " teachers=" + teachers.Count);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GroupRoles", "GetChatAdministrators failed, chat=" + chatId + ": " + ex.Message);
            roles.At = NowIran();   // ۱۰ دقیقه صبر تا تلاش بعدی (Loaded=false می‌مونه → کسی student ثبت نمی‌شه)
        }
        finally
        {
            roles.Refreshing = false;
        }

        return roles;
    }

    // لاگ نقش‌ها رو برای هر کاربر/نقش حداکثر یک بار در GroupRolesCacheMinutes می‌نویسه
    static bool ShouldLogRole(long chatId, string role, long userId)
    {
        string key = chatId.ToString(CultureInfo.InvariantCulture) + ":" + role + ":" +
                     userId.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        if (RoleLogAt.TryGetValue(key, out var at) && (now - at).TotalMinutes < GroupRolesCacheMinutes)
            return false;

        if (RoleLogAt.Count > 500)
            foreach (var kv in RoleLogAt)
                if ((now - kv.Value).TotalMinutes > 60) RoleLogAt.TryRemove(kv.Key, out _);

        RoleLogAt[key] = now;
        return true;
    }

    // پیام گروه: ادمین/مالک = staff، بقیه = زبان‌آموز (بی‌صدا؛ هیچ جوابی به گروه نمی‌ره).
    // برای زبان‌آموز، Student_Info آپ‌سرت می‌شه (نسخه حافظه‌ای + لاگ).
    static async Task ClassifyGroupMember(Message message)
    {
        try
        {
            if (message.From == null) return;
            var chatType = message.Chat.Type;
            if (chatType != ChatType.Group && chatType != ChatType.Supergroup) return;

            long chatId = message.Chat.Id;
            var from = message.From;
            if (from.IsBot || from.Id == BotUserId) return;   // بات‌ها هیچ‌وقت زبان‌آموز نمی‌شن

            var roles = await GetGroupRoles(chatId);

            if (!roles.Loaded)
            {
                // نقش‌ها خونده نشد → هیچ‌کس به‌عنوان زبان‌آموز ثبت نمی‌شه (ادمین‌ها هم امن می‌مونن)
                if (ShouldLogRole(chatId, "unknown", from.Id))
                    AppLog.Warn("GroupRoles", "roles not loaded, member not classified, chat=" + chatId, from.Id);
                return;
            }

            string classCode = roles.ClassCode;

            bool isOwner = roles.OwnerIds.Contains(from.Id);
            bool isTeacher = roles.TeacherIds.Contains(from.Id);
            if (isOwner || isTeacher || AdminIds.Contains(from.Id))
            {
                string role = isOwner || AdminIds.Contains(from.Id) ? "admin" : "teacher";
                if (ShouldLogRole(chatId, role, from.Id))
                    AppLog.Info("GroupRoles", role + " message, chat=" + chatId + " class=" +
                        (classCode.Length == 0 ? "-" : classCode), from.Id);
                string fullNameStaff = ((from.FirstName ?? "") + " " + (from.LastName ?? "")).Trim();
                UpsertGroupStaff(classCode, chatId, from.Id, fullNameStaff, from.Username ?? "", "", role);
                return;
            }

            if (classCode.Length == 0)
            {
                if (ShouldLogRole(chatId, "noclass", from.Id))
                    AppLog.Warn("GroupRoles",
                        "class_code not resolved (group title / registry), student upsert skipped, chat=" + chatId,
                        from.Id);
                return;
            }

            string fullName = ((from.FirstName ?? "") + " " + (from.LastName ?? "")).Trim();
            UpsertGroupStudent(classCode, chatId, from.Id, fullName, from.Username ?? "");
        }
        catch (Exception ex)
        {
            AppLog.Error("GroupRoles", "classify group member failed", ex, message.From?.Id);
        }
    }

    // آپ‌سرت زبان‌آموز گروه: نسخه حافظه‌ای + آینه در Student_Info (اکشن upsert_student).
    // سطح از Class_Registry/ترم فعال خوانده می‌شود و با معلوم‌شدن/تغییر آن دوباره نوشته می‌شود.
    static void UpsertGroupStudent(string classCode, long chatId, long userId, string fullName, string username)
    {
        string sid = userId.ToString(CultureInfo.InvariantCulture);
        string key = chatId.ToString(CultureInfo.InvariantCulture) + ":" + sid;
        bool isNew = false;
        string classLevel = GetClassLevel(classCode);

        var stu = GroupStudents.AddOrUpdate(key,
            _ =>
            {
                isNew = true;
                return new GroupStudent
                {
                    StudentId = sid,
                    ClassCode = classCode,
                    ChatId = chatId,
                    FullName = fullName,
                    Username = username,
                    Level = classLevel,
                    FirstSeen = NowIran(),
                    LastSeen = NowIran(),
                    MessageCount = 1
                };
            },
            (_, cur) =>
            {
                cur.ClassCode = classCode;
                if (!string.IsNullOrWhiteSpace(fullName)) cur.FullName = fullName;
                if (!string.IsNullOrWhiteSpace(username)) cur.Username = username;
                if (!string.IsNullOrWhiteSpace(classLevel)) cur.Level = classLevel;
                cur.LastSeen = NowIran();
                cur.MessageCount++;
                return cur;
            });

        // فقط وقتی سطح تازه معلوم/عوض شده در Student_Info بنویس (نه هر پیام گروه)
        if (!isNew && string.Equals(stu.Level ?? "", classLevel ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        if (isNew)
            AppLog.Info("GroupStudents", "student registered: class=" + classCode + " chat=" + chatId +
                " student_id=" + stu.StudentId + " name=" + (stu.FullName.Length == 0 ? "-" : stu.FullName), userId);

        // آینه‌کردن زبان‌آموز در Student_Info (بی‌صدا؛ هیچ پیام تلگرامی فرستاده نمی‌شه)
        PostInBackground(new
        {
            action = "upsert_student",
            student_id = stu.StudentId,
            class_code = stu.ClassCode,
            full_name = stu.FullName,
            level = stu.Level
        });
    }
    static void UpsertGroupStaff(string classCode, long chatId, long userId, string fullName, string username,
        string adminTitle = "", string role = "")
    {
        if (userId == 0 || string.IsNullOrWhiteSpace(classCode)) return;
        if (userId == BotUserId) return;

        string key = chatId.ToString(CultureInfo.InvariantCulture) + ":" + userId.ToString(CultureInfo.InvariantCulture);
        bool isNew = false;

        GroupStaffMap.AddOrUpdate(key,
            _ =>
            {
                isNew = true;
                return new GroupStaff
                {
                    UserId = userId,
                    ClassCode = classCode.Trim(),
                    FullName = fullName ?? "",
                    Username = username ?? "",
                    Title = adminTitle ?? "",
                    Role = role ?? "",
                    FirstSeen = NowIran(),
                    LastSeen = NowIran()
                };
            },
            (_, old) =>
            {
                old.LastSeen = NowIran();
                if (!string.IsNullOrWhiteSpace(fullName)) old.FullName = fullName;
                if (!string.IsNullOrWhiteSpace(adminTitle)) old.Title = adminTitle;
                if (!string.IsNullOrWhiteSpace(role)) old.Role = role;
                return old;
            });

        EnsureTeacherClass(userId, classCode, fullName);

        if (isNew)
            AppLog.Info("GroupStaff", "staff registered: user=" + userId + " class=" + classCode +
                " role=" + role + " title=" + adminTitle, userId);
    }

    static void EnsureTeacherClass(long userId, string classCode, string fullName)
    {
        classCode = (classCode ?? "").Trim();
        if (userId == 0 || classCode.Length == 0) return;

        string level = "";
        try { level = GetClassLevel(classCode) ?? ""; } catch (Exception ex) { AppLog.Warn(nameof(EnsureTeacherClass), ex.Message); }

        string teacherName = string.IsNullOrWhiteSpace(fullName) ? ("Teacher " + userId) : fullName;

        if (!Teachers.TryGetValue(userId, out var list) || list == null)
        {
            list = new List<TeacherClass>();
            Teachers[userId] = list;
        }

        if (!list.Any(c => Eq(c.ClassCode, classCode)))
        {
            list.Add(new TeacherClass
            {
                ClassCode = classCode,
                TeacherName = teacherName,
                Level = level
            });
            AppLog.Info("GroupStaff", "Teachers map + class " + classCode + " for " + userId, userId);
        }
    }

    // یک خط خلاصه برای /groupid: نقش فرستنده (staff یا student) + تعداد ادمین‌ها/زبان‌آموزها
    static string RoleSummaryLine(Message message, GroupRoles roles)
    {
        long uid = message.From?.Id ?? 0;
        bool isAdmin = uid != 0 && (roles.OwnerIds.Contains(uid) || IsAcademyAdmin(uid));
        bool isTeacher = uid != 0 && roles.TeacherIds.Contains(uid);
        string role = isAdmin ? "مدیر آکادمی (admin)" : isTeacher ? "مدرس (teacher)" : "زبان‌آموز (student)";

        string prefix = message.Chat.Id.ToString(CultureInfo.InvariantCulture) + ":";
        int students = 0;
        foreach (var key in GroupStudents.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) students++;

        return "\n\nنقش شما در این گروه: " + role +
               "\nمدیر آکادمی: " + roles.OwnerIds.Count +
               "\nمدرس‌ها: " + roles.TeacherIds.Count +
               "\nزبان‌آموزهای ثبت‌شده: " + students;
    }

    // توضیحات گروه: "level=101;term=2026-1;teacher_id=107592700;teacher_name=Ali" -> دیکشنری
    static Dictionary<string, string> ParseGroupDescription(string? description)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (description ?? "").Split(new[] { ';', '\n', '\r' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part.Substring(0, eq).Trim().ToLowerInvariant();
            string val = part.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            map[key] = val;
        }
        return map;
    }

    static string DictStr(Dictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";
    }

    static string ShowOrDash(Dictionary<string, string> map, string key)
    {
        string v = DictStr(map, key);
        return v.Length == 0 ? "—" : v;
    }

    // ثبت/به‌روزرسانی کلاس در حافظه (از توضیحات گروه یا /syncgroup)
    static ClassRegistration? RegisterClass(string? classCode, long groupChatId, string? level, string? term,
        long teacherId, string? teacherName, bool? active, string source)
    {
        string cc = (classCode ?? "").Trim();
        if (cc.Length == 0) return null;

        ClassRegistration reg;
        lock (ClassRegistryLock)
        {
            ClassRegistry.TryGetValue(cc, out var cur);
            reg = cur == null ? new ClassRegistration { ClassCode = cc } : cur.Clone();

            if (groupChatId != 0) reg.GroupChatId = groupChatId;
            if (!string.IsNullOrWhiteSpace(level)) reg.Level = level.Trim();
            if (!string.IsNullOrWhiteSpace(term)) reg.Term = term.Trim();
            if (teacherId != 0) reg.TeacherId = teacherId;
            if (!string.IsNullOrWhiteSpace(teacherName)) reg.TeacherName = teacherName.Trim();
            if (active.HasValue) reg.Active = active.Value;
            reg.Source = source;
            reg.UpdatedAt = NowIran();
            ClassRegistry[cc] = reg;

            // مدرس از توضیحات گروه: Teachers هم به‌روز می‌شود تا /classdone بدون hardcode کار کند.
            // لیست را کپی و جایگزین می‌کنیم (نه تغییر درجا) تا خواندن هم‌زمان امن بماند.
            if (reg.TeacherId != 0)
            {
                var old = Teachers.TryGetValue(reg.TeacherId, out var l) ? l : new List<TeacherClass>();
                var copy = new List<TeacherClass>(old);
                var existing = copy.FirstOrDefault(c =>
                    string.Equals(c.ClassCode, cc, StringComparison.OrdinalIgnoreCase));
                string effLevel = string.IsNullOrWhiteSpace(reg.Level) ? (existing?.Level ?? "") : reg.Level;
                string effName = string.IsNullOrWhiteSpace(reg.TeacherName)
                    ? (existing?.TeacherName ?? "")
                    : reg.TeacherName;
                if (existing == null)
                    copy.Add(new TeacherClass { ClassCode = cc, TeacherName = effName, Level = effLevel });
                else
                {
                    existing.TeacherName = effName;
                    existing.Level = effLevel;
                }
                Teachers[reg.TeacherId] = copy;
            }
        }

        AppLog.Info("ClassRegistry", "class " + cc + " => chat_id " + reg.GroupChatId + " level=" + reg.Level +
            " teacher=" + (reg.TeacherId == 0 ? "-" : reg.TeacherId.ToString(CultureInfo.InvariantCulture)) +
            " source=" + source);
        return reg;
    }

    // رجیستری اولیه از جدول‌های دستی (تا وقتی گروه‌ها ثبت نشده‌اند هم چیزی نشکند)
    static void SeedClassRegistry()
    {
        foreach (var kv in ClassGroupChatIds)
            if (kv.Value != 0) RegisterClass(kv.Key, kv.Value, null, null, 0, null, null, "config");

        foreach (var kv in Teachers)
            foreach (var tc in kv.Value)
                RegisterClass(tc.ClassCode, 0, tc.Level, null, kv.Key, tc.TeacherName, null, "config");
    }

    // رجیستری ذخیره‌شده در شیت را بعد از ری‌استارت برمی‌گرداند تا group_chat_id تکلیف گم نشود.
    static async Task LoadClassRegistryFromServer()
    {
        try
        {
            var res = await PostToAppsScriptWithResult(new { action = "list_class_registry" });
            if (!IsOk(res) || !res!.Value.TryGetProperty("items", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                AppLog.Warn("ClassRegistry", "list_class_registry unavailable; using memory only");
                return;
            }

            int loaded = 0;
            foreach (var it in arr.EnumerateArray())
            {
                string cc = JStr(it, "class_code");
                if (string.IsNullOrWhiteSpace(cc)) continue;
                long.TryParse(JStr(it, "group_chat_id"), out long gid);
                long.TryParse(JStr(it, "teacher_id"), out long tid);
                bool? active = null;
                string a = JStr(it, "active").ToLowerInvariant();
                if (a.Length > 0) active = a == "yes" || a == "1" || a == "true" || a == "on";
                RegisterClass(cc, gid, JStr(it, "level"), JStr(it, "term"), tid,
                    JStr(it, "teacher_name"), active, "sheet");
                loaded++;
            }
            AppLog.Info("ClassRegistry", "loaded " + loaded + " classes from sheet");
        }
        catch (Exception ex)
        {
            AppLog.Warn("ClassRegistry", "load from sheet failed: " + ex.Message);
        }
    }

    // ذخیره رجیستری در شیت از طریق Apps Script.
    // اگر نسخه Deploy شده این action را نداشته باشد فقط Warn می‌شود و ثبت حافظه‌ای سر جایش می‌ماند.
    static async Task<bool> PushClassRegistry(ClassRegistration reg)
    {
        var res = await PostToAppsScriptWithResult(new
        {
            action = "upsert_class_registry",
            class_code = reg.ClassCode,
            group_chat_id = reg.GroupChatId.ToString(CultureInfo.InvariantCulture),
            level = reg.Level,
            term = reg.Term,
            teacher_id = reg.TeacherId == 0 ? "" : reg.TeacherId.ToString(CultureInfo.InvariantCulture),
            teacher_name = reg.TeacherName,
            active = reg.Active ? "yes" : "no",
            source = reg.Source
        });

        if (IsOk(res)) return true;

        string why = res.HasValue ? JStr(res.Value, "error") : "no response";
        AppLog.Warn("ClassRegistry", "upsert_class_registry failed for class " + reg.ClassCode + ": " +
            (string.IsNullOrWhiteSpace(why) ? "ok=false" : why));
        return false;
    }

    // ثبت از روی عنوان/توضیحات گروه (وقتی بات اضافه می‌شود یا /syncgroup زده می‌شود)
    static async Task<(bool Ok, string Message)> RegisterClassFromGroup(long chatId, string source)
    {
        string title;
        string description;
        try
        {
            var full = await bot.GetChat(chatId);
            title = (full.Title ?? "").Trim();
            description = full.Description ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Error("ClassRegistry", "GetChat failed, chat=" + chatId + " source=" + source, ex);
            return (false, "اطلاعات گروه خوانده نشد. مطمئن شو بات عضو گروه است.");
        }

        if (title.Length == 0)
            return (false, "عنوان گروه خالی است. عنوان گروه باید کد کلاس باشد (نمونه: CLASS-CODE).");

        // توضیحات گروه اختیاری است. سطح/ترم/برنامه از «شروع ترم» و Class_Registry می‌آید.
        var f = ParseGroupDescription(description);
        string teacherName = DictStr(f, "teacher_name");
        string teacherRaw = DictStr(f, "teacher_id");

        long teacherId = 0;
        if (teacherRaw.Length > 0 && !long.TryParse(teacherRaw, out teacherId))
        {
            AppLog.Warn("ClassRegistry", "teacher_id is not a number, class=" + title + " value=" + teacherRaw);
            teacherId = 0;
        }
        if (teacherId == 0)
            AppLog.Warn("ClassRegistry", "no teacher_id in group description, class=" + title + " (/classdone needs it)");

        bool? active = null;
        string activeRaw = DictStr(f, "active").ToLowerInvariant();
        if (activeRaw.Length > 0)
            active = activeRaw == "yes" || activeRaw == "1" || activeRaw == "true" || activeRaw == "on";

        var reg = RegisterClass(title, chatId, null, null, teacherId, teacherName, active, source);
        if (reg == null) return (false, "کد کلاس نامعتبر است.");

        bool pushed = await PushClassRegistry(reg);

        var sb = new StringBuilder();
        sb.Append("کلاس " + reg.ClassCode + " ثبت شد.");
        sb.Append("\nchat_id: " + chatId);
        sb.Append("\nlevel: " + (string.IsNullOrWhiteSpace(reg.Level) ? "از «شروع ترم» تنظیم می‌شود" : reg.Level));
        if (!string.IsNullOrWhiteSpace(reg.Term)) sb.Append("\nterm: " + reg.Term);
        sb.Append("\nteacher_id: " + (reg.TeacherId == 0 ? "—" : reg.TeacherId.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(reg.TeacherName)) sb.Append("\nteacher_name: " + reg.TeacherName);
        if (!reg.Active) sb.Append("\nactive: no (این گروه برای ارسال استفاده نمی‌شود)");
        sb.Append("\n(توضیحات گروه لازم نیست؛ عنوان گروه = کد کلاس)");
        sb.Append(pushed ? "\nشیت: ذخیره شد ✅" : "\nشیت: ذخیره نشد ⚠️ (فقط در حافظه؛ لاگ را ببین)");
        return (true, sb.ToString());
    }

    // وقتی بات به گروه اضافه/حذف می‌شود (my_chat_member)
    static async Task HandleMyChatMember(ChatMemberUpdated upd)
    {
        try
        {
            if (upd.NewChatMember?.User == null || upd.NewChatMember.User.Id != BotUserId) return; // فقط خود بات

            var st = upd.NewChatMember.Status;
            bool inGroup = st == ChatMemberStatus.Member || st == ChatMemberStatus.Administrator ||
                           st == ChatMemberStatus.Creator;
            if (!inGroup) return;   // بات از گروه خارج/اخراج شد
            if (upd.Chat.Type != ChatType.Group && upd.Chat.Type != ChatType.Supergroup) return;

            var (ok, msg) = await RegisterClassFromGroup(upd.Chat.Id, "my_chat_member");

            // پیام کوتاه در گروه تا ادمین بداند چه چیزی کم است (بعد از چند دقیقه پاک می‌شود)
            try
            {
                var sent = await bot.SendMessage(upd.Chat.Id, (ok ? "✅ " : "⚠️ ") + msg);
                DeleteLater(upd.Chat.Id, sent.Id, TimeSpan.FromMinutes(GroupNoticeDeleteMinutes));
            }
            catch (Exception ex)
            {
                AppLog.Warn("ClassRegistry", "group reply failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("MyChatMember", "register from group failed", ex);
        }
    }

    // متن اطلاعات گروه برای /groupid
    static async Task<string> BuildGroupInfoText(long chatId)
    {
        string title = "";
        string description = "";
        try
        {
            var full = await bot.GetChat(chatId);
            title = (full.Title ?? "").Trim();
            description = full.Description ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Warn("GroupInfo", "GetChat failed, chat=" + chatId + ": " + ex.Message);
        }

        var f = ParseGroupDescription(description);
        var sb = new StringBuilder();
        sb.Append("chat_id این گروه: " + chatId);
        sb.Append("\nعنوان (class_code): " + (title.Length == 0 ? "—" : title));
        sb.Append("\nteacher_id: " + ShowOrDash(f, "teacher_id"));
        sb.Append("\nteacher_name: " + ShowOrDash(f, "teacher_name"));
        sb.Append("\nactive: " + ShowOrDash(f, "active"));

        if (title.Length == 0)
            sb.Append("\nوضعیت رجیستری: نامشخص (عنوان گروه خوانده نشد)");
        else if (ClassRegistry.TryGetValue(title, out var reg))
            sb.Append("\nوضعیت رجیستری: ثبت شده ✅ (level: " +
                (string.IsNullOrWhiteSpace(reg.Level) ? "از شروع ترم" : reg.Level) + ")");
        else
            sb.Append("\nوضعیت رجیستری: ثبت نشده ❌");

        sb.Append("\n\nتوضیحات گروه اختیاری است (Teacher/Time/sessions فقط متن است).");
        sb.Append("\nسطح، یونیت، پکیج، نوع کلاس و برنامه از «شروع ترم» خوانده می‌شود.");
        sb.Append("\nاگر مدرس را می‌خواهی از توضیحات بخوانی: teacher_id=...;teacher_name=...");
        return sb.ToString();
    }

    static async Task HandleGroupCommand(Message message, string text)
    {
        // نقش فرستنده در گروه: ادمین/مالک = staff، بقیه = زبان‌آموز (بی‌صدا، بدون جواب)
        await ClassifyGroupMember(message);

        // اطلاعات گروه (chat_id + فیلدهای توضیحات)
        if (IsCommand(text, "/groupid"))
        {
            var roles = await GetGroupRoles(message.Chat.Id);
            await Send(message.Chat.Id,
                await BuildGroupInfoText(message.Chat.Id) + RoleSummaryLine(message, roles));
            return;
        }

        // ثبت دستی همان منطق خودکار (بعد از اصلاح عنوان/توضیحات گروه)
        if (IsCommand(text, "/syncgroup"))
        {
            var (syncOk, syncMsg) = await RegisterClassFromGroup(message.Chat.Id, "syncgroup");
            // کش ادمین‌ها و class_code هم همین‌جا رفرش می‌شه
            var roles = await GetGroupRoles(message.Chat.Id, force: true);
            await Send(message.Chat.Id, (syncOk ? "✅ " : "⚠️ ") + syncMsg +
                "\n\n" + await BuildGroupInfoText(message.Chat.Id) + RoleSummaryLine(message, roles));
            return;
        }

        string? payload = null;
        string label = "";
        if (IsCommand(text, "/practice")) { payload = "practice"; label = "📚 شروع تمرین"; }
        else if (IsCommand(text, "/homework")) { payload = "homework"; label = "📝 ارسال تکلیف"; }
        else if (IsCommand(text, "/classdone")) { payload = "classdone"; label = "🎓 ثبت جلسه کلاس"; }
        else if (IsCommand(text, "/feedback_teacher")) { payload = "feedback_teacher"; label = "⭐ فیدبک به مدرس"; }
        else if (IsCommand(text, "/feedback_student")) { payload = "feedback_student"; label = "📋 فیدبک به زبان‌آموز"; }
        else if (IsCommand(text, "/myresult")) { payload = "myresult"; label = "📊 نتیجه ترم"; }
        else if (IsCommand(text, "/classresults")) { payload = "classresults"; label = "📈 نتایج کلاس"; }
        else if (IsCommand(text, "/placement")) { payload = "placement"; label = "🎯 تعیین سطح"; }
        else if (IsCommand(text, "/start")) { payload = "start"; label = "💬 رفتن به چت خصوصی"; }

        if (payload == null || string.IsNullOrEmpty(BotUsername)) return;

        string url = "https://t.me/" + BotUsername + "?start=" + payload;
        string name = message.From?.FirstName ?? "";
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl(label + " (چت خصوصی)", url) }
        });

        var sent = await bot.SendMessage(message.Chat.Id,
            name + "، برای ادامه دکمه زیر را بزن تا در چت خصوصی کار کنیم 👇\n" +
            "(این پیام " + GroupNoticeDeleteMinutes + " دقیقه دیگر پاک می‌شود.)",
            replyMarkup: markup);

        DeleteLater(message.Chat.Id, sent.Id, TimeSpan.FromMinutes(GroupNoticeDeleteMinutes));
    }

    // پاک کردن پیام بعد از مدتی (اگر بات ری‌استارت بشه، پیام‌های در صف پاک نمی‌شن)
    static void DeleteLater(long chatId, int messageId, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await bot.DeleteMessage(chatId, messageId);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DeleteLater", ex.Message);
            }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // ===================== REMINDERS =====================

    static bool JBool(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object &&
               e.TryGetProperty(name, out var p) &&
               p.ValueKind == JsonValueKind.True;
    }

    // ===================== TERM / RECOMPUTE =====================

    static string TermFor(string? classCode) =>
        (!string.IsNullOrWhiteSpace(classCode) && ClassTermIds.TryGetValue(classCode, out var t) && !string.IsNullOrWhiteSpace(t))
            ? t : ActiveTermId;

    // پیام «با تأخیر» وقتی سرور نتوانسته نتایج را به‌روز کند
    static string StaleNote(JsonElement? res) =>
        (res.HasValue && JBool(res.Value, "stale"))
            ? "\n\n⏳ به‌روزرسانی نتیجه همین حالا ممکن نشد؛ ممکن است این داده با تأخیر باشد."
            : "";

    // محاسبه مجدد نتایج یک کلاس. هیچ‌وقت exception بیرون نمی‌دهد (فقط AppLog).
    static async Task<bool> RecomputeForClass(string? classCode, string place, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(classCode)) return false;
        try
        {
            if (!force && LastRecomputeAt.TryGetValue(classCode, out var at) &&
                (DateTime.UtcNow - at).TotalSeconds < RecomputeThrottleSeconds)
                return true;

            LastRecomputeAt[classCode] = DateTime.UtcNow;
            var res = await PostToAppsScriptWithResult(new
            {
                action = "recompute_term_results",
                class_code = classCode,
                term_hint = TermFor(classCode)
            });

            if (IsOk(res)) return true;

            LastRecomputeAt.TryRemove(classCode, out _);
            AppLog.Warn(place, "recompute_term_results failed, class=" + classCode + " err=" +
                (res.HasValue ? JStr(res.Value, "error") : "no response"));
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error(place, "recompute_term_results exception, class=" + classCode, ex);
            return false;
        }
    }

    // همان، ولی بدون منتظر ماندن (classdone / تکلیف / فیدبک)
    static void RecomputeInBackground(string? classCode, string place, bool force = false)
    {
        _ = Task.Run(() => RecomputeForClass(classCode, place, force)).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    static async Task ReminderLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await CheckReminders();
            }
            catch (Exception ex)
            {
                AppLog.Error("ReminderLoop", "exception", ex);
            }
        }
    }

    static async Task CheckReminders()
    {
        var now = NowIran();
        if (ReminderSkipDays.Contains(now.DayOfWeek)) return;

        var start = new TimeSpan(ReminderHour, ReminderMinute, 0);
        if (now.TimeOfDay < start || now.TimeOfDay > start + TimeSpan.FromMinutes(ReminderWindowMinutes)) return;

        string today = TodayStr();
        if (LastReminderDate == today) return;

        // قبل از ارسال علامت می‌زنیم تا تکراری نره
        LastReminderDate = today;
        try { System.IO.File.WriteAllText(ReminderStateFile, today); } catch (Exception ex) { AppLog.Warn(nameof(CheckReminders), ex.Message); }

        var result = await PostToAppsScriptWithResult(new
        {
            action = "get_reminders",
            max_homework_age_days = HomeworkMaxAgeDays
        });

        if (!IsOk(result) || !result!.Value.TryGetProperty("students", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            AppLog.Warn("Reminders", "could not load the list, will retry");
            LastReminderDate = "";
            try { System.IO.File.Delete(ReminderStateFile); } catch (Exception ex) { AppLog.Warn(nameof(CheckReminders), ex.Message); }
            return;
        }

        int sent = 0, failed = 0;
        foreach (var s in arr.EnumerateArray())
        {
            if (!long.TryParse(JStr(s, "student_id"), out long targetChat)) continue;

            bool practicePending = JBool(s, "practice_pending");
            bool hwPending = JBool(s, "homework_pending");
            bool fbRequired = JBool(s, "feedback_required");

            // اگر جلسه تمرین باز است، یادآوری تمرین را نفرست (مثل CheckDailyGrammar)
            if (practicePending && PracticeStates.TryGetValue(targetChat, out var pstRem) &&
                pstRem.Index < pstRem.Questions.Count)
            {
                AppLog.Info("Reminders", "practice reminder skipped (active practice session)", targetChat);
                practicePending = false;
            }

            if (!practicePending && !hwPending && !fbRequired) continue;

            string name = JStr(s, "name");
            var sb = new StringBuilder();
            sb.Append("⏰ یادآوری\n");
            sb.Append(string.IsNullOrWhiteSpace(name) ? "سلام!" : "سلام " + name + "!");
            sb.Append("\n\n");

            if (fbRequired)
                sb.Append("• برای استفاده از بات در ترم جدید باید اول فیدبک ناشناس ترم قبل را به مدرس ثبت کنی: /feedback_teacher\n");

            if (practicePending)
                sb.Append("• امروز هنوز تمرینت را انجام ندادی: /practice\n");

            if (hwPending)
            {
                string unit = JStr(s, "homework_unit");
                string lesson = JStr(s, "homework_lesson");
                string ul = unit + (string.IsNullOrWhiteSpace(lesson) ? "" : "_" + lesson);
                var assignedN = JNum(s, "homework_assigned");
                var submittedN = JNum(s, "homework_submitted");
                if (assignedN.HasValue && submittedN.HasValue)
                    sb.Append("• سهمیه تکلیف درس " + ul + " کامل نشده (" +
                        submittedN.Value.ToString("0", CultureInfo.InvariantCulture) + " از " +
                        assignedN.Value.ToString("0", CultureInfo.InvariantCulture) + "): /homework\n");
                else
                    sb.Append("• تکلیف درس " + ul + " هنوز ثبت نشده: /homework\n");
            }

            try
            {
                await bot.SendMessage(targetChat, sb.ToString());
                sent++;
            }
            catch (Exception ex)
            {
                // معمولاً یعنی زبان‌آموز هنوز بات را Start نکرده یا بلاکش کرده
                failed++;
                AppLog.Warn("Reminders", "send failed: " + ex.Message, targetChat);
            }

            await Task.Delay(100);
        }

        AppLog.Info("Reminders", "done. sent=" + sent + " failed=" + failed);
    }

    static async Task DailyGrammarLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                await CheckDailyGrammar();
            }
            catch (Exception ex)
            {
                AppLog.Error("DailyGrammarLoop", "exception", ex);
            }
        }
    }

    static async Task CheckDailyGrammar()
    {
        var now = NowIran();
        if (now.TimeOfDay < new TimeSpan(18, 0, 0) || now.TimeOfDay > new TimeSpan(21, 0, 0)) return;

        string today = TodayStr();
        if (LastDailyGrammarDate == today)
        {
            if (LastDailyGrammarSkipLog != today)
            {
                LastDailyGrammarSkipLog = today;
                AppLog.Info("DailyGrammar", "skipped: auto-send already ran today " + today);
            }
            return;
        }

        AppLog.Info("DailyGrammar", "auto-send starting for " + today);
        LastDailyGrammarDate = today;
        try { System.IO.File.WriteAllText(DailyGrammarStateFile, today); } catch (Exception ex) { AppLog.Warn(nameof(CheckDailyGrammar), ex.Message); }

        var res = await PostToAppsScriptWithResult(new { action = "get_daily_grammar_targets" });
        if (!IsOk(res) || !res!.Value.TryGetProperty("students", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            AppLog.Warn("DailyGrammar", "could not load targets, will retry");
            if (LastDailyGrammarAlertDate != today)
            {
                LastDailyGrammarAlertDate = today;
                await NotifyAdminsDailyGrammar("⚠️ گرامر روز " + today + " کامل ارسال نشد.\n" +
                    "لیست زبان‌آموزان بارگذاری نشد (چند بار دیگر تلاش می‌شود).");
            }
            LastDailyGrammarDate = "";
            try { System.IO.File.Delete(DailyGrammarStateFile); } catch (Exception ex) { AppLog.Warn(nameof(CheckDailyGrammar), ex.Message); }
            return;
        }

        int sent = 0, failed = 0;
        var failedIds = new List<string>();
        foreach (var s in arr.EnumerateArray())
        {
            string studentId = JStr(s, "student_id");
            if (!long.TryParse(studentId, out long chatId))
            {
                AppLog.Warn("DailyGrammar", "skipped: invalid student_id " + studentId);
                continue;
            }
            string classCode = JStr(s, "class_code");
            string level = JStr(s, "level");
            if (string.IsNullOrWhiteSpace(level)) level = GetClassLevel(classCode);
            string unitLesson = NormalizeUnitLesson(JStr(s, "unit"), JStr(s, "lesson"), "1_1");

            // اگر جلسه تمرین باز است، سوال گرامر نفرست تا جواب زبان‌آموز
            // اشتباهی در گرامر مصرف نشود و جلسه تمرین معطل نماند.
            if (PracticeStates.TryGetValue(chatId, out var pst) && pst.Index < pst.Questions.Count)
            {
                AppLog.Info("DailyGrammar", "skipped (active practice session)", chatId);
                continue;
            }

            try
            {
                await SendDailyGrammarToStudent(chatId, chatId, studentId, classCode, level, unitLesson, false, true);
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                failedIds.Add(studentId + (string.IsNullOrWhiteSpace(classCode) ? "" : " / " + classCode));
                AppLog.Warn("DailyGrammar", "send failed: " + ex.Message, chatId);
            }
            await Task.Delay(100);
        }

        if ((failed > 0 || (sent == 0 && arr.GetArrayLength() > 0)) && LastDailyGrammarAlertDate != today)
        {
            LastDailyGrammarAlertDate = today;
            await NotifyAdminsDailyGrammar("⚠️ گرامر روز " + today + " کامل ارسال نشد.\n" +
                "ارسال موفق: " + sent + " | ناموفق: " + failed + "\n" +
                (failedIds.Count > 0 ? "زبان‌آموزان ناموفق: " + string.Join(", ", failedIds) : ""));
        }

        AppLog.Info("DailyGrammar", "done. sent=" + sent + " failed=" + failed);
    }

    // اطلاع به ادمین‌ها؛ هر ارسال جدا try/catch می‌خورد تا حلقه گرامر خراب نشود
    static async Task NotifyAdminsDailyGrammar(string text)
    {
        foreach (var adminId in AdminIds.Distinct())
        {
            try
            {
                if (bot != null) await bot.SendMessage(adminId, text);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DailyGrammar", "admin notify failed: " + ex.Message, adminId);
            }
        }
    }

    // ===================== FEEDBACK (فیدبک) =====================

    static FbCriterion Crit(string key, string title, string question, string note, params (string Short, string Full)[] opts)
    {
        var c = new FbCriterion { Key = key, Title = title, Question = question, Note = note };
        for (int i = 0; i < 5; i++) { c.Short[i] = opts[i].Short; c.Full[i] = opts[i].Full; }
        return c;
    }

    // گزینه: متن کوتاه (روی دکمه) و متن کامل (تو پیام و شیت)
    static (string Short, string Full) O(string text) => (text, text);
    static (string Short, string Full) O(string shortText, string fullText) => (shortText, fullText);

    // فیدبک زبان‌آموز به مدرس (ناشناس): ۷ معیار
    private static readonly FbCriterion[] TeacherCriteria =
    {
        Crit("knowledge", "سطح دانش مدرس", "تسلط مدرس بر زبان و مفاهیم آموزشی چقدر بود؟", "",
            O("ضعیف", "ضعیف: عدم تسلط بر مفاهیم پایه"),
            O("متوسط رو به پایین", "متوسط رو به پایین: دانش کافی با تردید در مباحث پیچیده"),
            O("قابل قبول", "قابل قبول: تسلط استاندارد"),
            O("خوب", "خوب: تسلط بالا و توضیح عمیق‌تر"),
            O("عالی", "عالی: تسلط کامل و دانش گسترده")),

        Crit("environment", "فضای یادگیری کلاس", "پویایی و کیفیت محیط کلاس چطور بود؟", "",
            O("یکنواخت/خسته‌کننده"),
            O("ایستا و کم‌انگیزه"),
            O("متعادل و استاندارد"),
            O("پویا و فعال"),
            O("بسیار محرک و لذت‌بخش")),

        Crit("homework_followup", "پیگیری تکالیف", "مدرس تکالیف و یادگیری بیرون کلاس را چطور پیگیری می‌کرد؟", "",
            O("عدم پیگیری"),
            O("نامنظم و سطحی"),
            O("استاندارد"),
            O("دقیق با نکات اصلاحی"),
            O("تحلیلی و شخصی‌سازی‌شده")),

        Crit("rapport", "جو و ارتباط مدرس", "حس راحتی و ارتباط با مدرس چطور بود؟", "",
            O("سرد و فاصله‌دار"),
            O("رسمی بیش از حد"),
            O("محترمانه، کم‌صمیمیت", "محترمانه ولی بدون صمیمیت زیاد"),
            O("گرم و دوستانه"),
            O("بسیار صمیمی و امن")),

        Crit("persian_use", "استفاده از زبان فارسی", "میزان استفاده مدرس از فارسی در کلاس چقدر بود؟",
            "توجه: مقیاس معکوس است (۵ = بهترین برای immersion)",
            O("بسیار زیاد", "بسیار زیاد: بیشتر فارسی"),
            O("زیاد"),
            O("متعادل", "متعادل: فقط برای موارد پیچیده"),
            O("کم"),
            O("بسیار کم/صفر", "بسیار کم/صفر: کلاس تقریباً کامل انگلیسی")),

        Crit("ethics", "اخلاق و رفتار حرفه‌ای", "رفتار و برخورد مدرس چطور بود؟", "",
            O("نامناسب"),
            O("ضعیف"),
            O("متعادل و محترمانه"),
            O("خوب، صبور و حمایتگر"),
            O("الگو و بی‌نقص")),

        Crit("reenroll", "انتخاب مدرس برای ترم بعد", "چقدر مایلی ترم بعد با همین مدرس ادامه بدهی؟", "",
            O("قطعاً خیر"),
            O("احتمالاً خیر"),
            O("خنثی / بستگی دارد"),
            O("احتمالاً بله"),
            O("قطعاً بله")),
    };

    // فیدبک مدرس به زبان‌آموز: ۱۰ معیار
    private static readonly FbCriterion[] StudentCriteria =
    {
        Crit("overall", "پیشرفت کلی (Overall Progress)", "", "",
            O("عدم پیشرفت/پسرفت", "عدم پیشرفت یا پسرفت؛ ناتوان در یادآوری مطالب"),
            O("کند و ناپیوسته", "پیشرفت کند و ناپیوسته"),
            O("در حد انتظار", "پیشرفت در حد انتظار"),
            O("سریع‌تر از انتظار", "پیشرفت سریع‌تر از انتظار"),
            O("چشمگیر", "پیشرفت چشمگیر و فراتر از انتظار")),

        Crit("speaking", "مهارت صحبت کردن (Speaking)", "", "",
            O("مکث طولانی/ناقص", "مکث‌های طولانی و جملات ناقص؛ اشتباهات مانع درک"),
            O("جملات کوتاه", "جملات کوتاه؛ نیاز به راهنمایی مداوم"),
            O("موضوعات ساده", "بیان منظور در موضوعات ساده با محدودیت"),
            O("روان، اشتباه جزئی", "صحبت روان با اشتباهات جزئی"),
            O("تسلط کامل", "تسلط کامل، ساختار پیچیده و لحن طبیعی")),

        Crit("listening", "مهارت شنیداری (Listening)", "", "",
            O("درک نمی‌کند", "عدم درک دستورالعمل ساده"),
            O("فقط کلمات کلیدی"),
            O("کلیات", "درک کلیات بدون جزئیات"),
            O("جزئیات خوب", "درک خوب جزئیات با تلاش کم"),
            O("درک عالی", "درک عالی incl. nuance و سرعت بالا")),

        Crit("reading", "مهارت خواندن (Reading)", "", "",
            O("کندخوانی شدید", "کندخوانی شدید؛ ناتوانی در مفهوم اصلی"),
            O("کلمه‌به‌کلمه", "خواندن کلمه‌به‌کلمه؛ درک خیلی محدود"),
            O("در حد انتظار", "سرعت و درک در حد انتظار"),
            O("خوب", "خواندن خوب و درک جزئیات"),
            O("سریع و دقیق", "خواندن سریع و درک دقیق")),

        Crit("writing", "مهارت نوشتن (Writing)", "", "",
            O("بسیار ضعیف", "نوشتن بسیار ضعیف و نامفهوم"),
            O("ساده، خطای زیاد", "جملات بسیار ساده با خطاهای زیاد"),
            O("قابل قبول", "نوشتن قابل قبول در سطح درس"),
            O("منسجم", "نوشتن منسجم با خطای کم"),
            O("دقیق و قوی", "نوشتن دقیق و قوی")),

        Crit("vocabulary", "دایره لغات (Vocabulary)", "", "",
            O("حتی پایه هم کم", "لغات پایه را هم کم به‌کار می‌برد"),
            O("محدود", "دایره لغات محدود"),
            O("مناسب سطح"),
            O("خوب و متنوع"),
            O("دقیق و غنی")),

        Crit("grammar", "دقت گرامری (Grammar)", "", "",
            O("خطای پایه مکرر", "خطاهای پایه مکرر و مانع ارتباط"),
            O("خطاهای زیاد"),
            O("قابل قبول"),
            O("خطای کم"),
            O("دقت بالا")),

        Crit("improvement", "نیازهای آموزشی (Area for Improvement)", "", "",
            O("نیاز فوری", "نیاز فوری به مداخله آموزشی"),
            O("ضعف مشخص", "نقاط ضعف مشخص و مانع پیشرفت"),
            O("تمرین هدفمند", "نیاز به تمرین هدفمند"),
            O("ضعف جزئی"),
            O("ضعف عمده ندارد")),

        Crit("participation", "مشارکت و نگرش (Participation & Attitude)", "", "",
            O("عدم علاقه", "عدم علاقه یا مقاومت"),
            O("مشارکت منفعل"),
            O("مشارکت معمولی"),
            O("مشتاق و داوطلب"),
            O("بسیار مشتاق", "بسیار مشتاق و مؤثر در کلاس")),

        Crit("assessment", "آمادگی برای ارزیابی (Review & Assessment)", "", "",
            O("عدم آمادگی", "عدم آمادگی؛ تکالیف انجام‌نشده"),
            O("آمادگی ناقص"),
            O("آمادگی متوسط"),
            O("آمادگی کامل"),
            O("فراتر از انتظار")),
    };

    // متن کامل یک معیار: عنوان، توضیح و هر ۵ گزینه (برای اینکه متن کامل دیده بشه، نه فقط روی دکمه)
    static string BuildCriterionText(string icon, int index, int total, FbCriterion c, string who)
    {
        var sb = new StringBuilder();
        sb.Append(icon + " معیار " + (index + 1) + " از " + total);
        if (!string.IsNullOrEmpty(who)) sb.Append("  ·  " + who);
        sb.Append("\n\n" + c.Title + "\n");
        if (!string.IsNullOrEmpty(c.Question)) sb.Append(c.Question + "\n");
        if (!string.IsNullOrEmpty(c.Note)) sb.Append(c.Note + "\n");
        sb.Append("\n");
        for (int i = 0; i < 5; i++) sb.Append((i + 1) + ") " + c.Full[i] + "\n");
        return sb.ToString().TrimEnd();
    }

    // پنج دکمه: «1 - ضعیف» ... (callback: prefix|نمره)
    static InlineKeyboardMarkup BuildScoreKeyboard(string prefix, FbCriterion c)
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < 5; i++)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData((i + 1) + " - " + c.Short[i], prefix + "|" + (i + 1)) });
        return new InlineKeyboardMarkup(rows);
    }

    // مدرس یک کلاس (از لیست Teachers)
    static (long Id, string Name)? FindTeacherForClass(string classCode)
    {
        foreach (var kv in Teachers)
        {
            var tc = kv.Value.FirstOrDefault(c => string.Equals(c.ClassCode, classCode, StringComparison.OrdinalIgnoreCase));
            if (tc != null) return (kv.Key, tc.TeacherName);
        }
        return null;
    }

    // ----- A) فیدبک زبان‌آموز به مدرس (ناشناس) -----

    static async Task StartTeacherFeedback(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /feedback_teacher را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        var student = await FindStudentFull(userId.ToString());
        if (student == null || string.IsNullOrWhiteSpace(student.ClassCode))
        {
            await Send(chatId, "برای ثبت فیدبک اول باید تو سیستم به‌عنوان زبان‌آموز ثبت باشی. با آموزشگاه هماهنگ کن.");
            return;
        }

        var teacher = FindTeacherForClass(student.ClassCode);
        if (teacher == null)
        {
            await Send(chatId, "مدرس این کلاس هنوز تو سیستم ثبت نشده.");
            return;
        }

        // آیا الان وقتشه؟ (بعد از جلسه آخر ترم) و آیا قبلاً ثبت کرده؟
        var chk = await PostToAppsScriptWithResult(new
        {
            action = "check_teacher_feedback",
            telegram_id = userId.ToString(),
            class_code = student.ClassCode
        });

        bool forPrevious = false;
        if (IsOk(chk) && chk!.Value.TryGetProperty("allowed", out var al))
        {
            if (al.ValueKind == JsonValueKind.False)
            {
                string reason = JStr(chk!.Value, "reason");
                if (reason == "not_yet")
                {
                    await Send(chatId,
                        "فیدبک به مدرس بعد از جلسه " + JStr(chk!.Value, "total") + " فعال می‌شود.\n" +
                        "الان " + JStr(chk!.Value, "held") + " جلسه از " + JStr(chk!.Value, "total") + " برگزار شده.\n\n" +
                        "ثبت این فیدبک بعد از جلسه آخر الزامی است و بدون آن نمی‌توانی در ترم بعد از بات استفاده کنی.");
                }
                else
                {
                    await Send(chatId, "فیدبک این ترم را قبلاً ثبت کرده‌ای. ممنون! 🙏");
                }
                return;
            }
            forPrevious = JBool(chk!.Value, "for_previous");
        }

        var state = new TeacherFbState
        {
            UserId = userId,
            StudentTelegramId = userId.ToString(),
            ClassCode = student.ClassCode,
            TeacherId = teacher.Value.Id,
            TeacherName = teacher.Value.Name
        };
        TeacherFbStates[userId] = state;

        await Send(chatId,
            "🔒 فیدبک شما کاملاً ناشناس ثبت می‌شود و مدرس متوجه نمی‌شود چه کسی آن را فرستاده است.\n\n" +
            "مدرس: " + state.TeacherName +
            (forPrevious ? "\n(این فیدبک مربوط به ترم قبل است.)" : ""));

        await AskTeacherFbCriterion(chatId, state);
    }

    static async Task AskTeacherFbCriterion(long chatId, TeacherFbState state)
    {
        var c = TeacherCriteria[state.Index];
        await bot.SendMessage(chatId,
            BuildCriterionText("⭐", state.Index, TeacherCriteria.Length, c, ""),
            replyMarkup: BuildScoreKeyboard("tf|" + state.Index, c));
    }

    static async Task AskTeacherFbContinue(long chatId, TeacherFbState state)
    {
        state.Step = "continue";
        state.WaitingText = false;
        await bot.SendMessage(chatId,
            "آیا مایل به ادامه در ترم بعد هستید؟",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ بله", "tf|cont|yes"),
                    InlineKeyboardButton.WithCallbackData("❌ خیر", "tf|cont|no"),
                    InlineKeyboardButton.WithCallbackData("🤔 هنوز مطمئن نیستم", "tf|cont|unsure")
                }
            }));
    }

    static async Task AskTeacherFbSatisfaction(long chatId, TeacherFbState state)
    {
        state.Step = "satisfaction";
        state.WaitingText = true;
        var values = new[] { "50", "60", "70", "80", "90", "100" };
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < values.Length; i += 3)
            rows.Add(values.Skip(i).Take(3)
                .Select(v => InlineKeyboardButton.WithCallbackData(v + "%", "tf|sat|" + v)).ToArray());
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("۰٪", "tf|sat|0") });
        await bot.SendMessage(chatId,
            "چند درصد از ترمی که گذشت رضایت دارید؟\nیک عدد ۰ تا ۱۰۰ بنویس یا یکی از دکمه‌ها را بزن.",
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandleTeacherFbCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;

        if (!TeacherFbStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /feedback_teacher بزن.");
            return;
        }

        var parts = cq.Data.Split('|');
        if (parts.Length != 3) { await bot.AnswerCallbackQuery(cq.Id); return; }

        await state.Gate.WaitAsync();
        try
        {
            if (parts[1] == "retry")
            {
                if (state.Step != "save") { await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه."); return; }
                await bot.AnswerCallbackQuery(cq.Id);
                await SaveTeacherFeedback(chatId, state);
                return;
            }

            if (parts[1] == "cont")
            {
                if (state.Step != "continue" || (parts[2] != "yes" && parts[2] != "no" && parts[2] != "unsure"))
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }
                state.ContinueNextTerm = parts[2];
                await bot.AnswerCallbackQuery(cq.Id);
                await AskTeacherFbSatisfaction(chatId, state);
                return;
            }

            if (parts[1] == "sat")
            {
                if (state.Step != "satisfaction" || !int.TryParse(parts[2], out int pct) || pct < 0 || pct > 100)
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }
                state.SatisfactionPercent = pct;
                state.WaitingText = false;
                state.Step = "save";
                await bot.AnswerCallbackQuery(cq.Id);
                await SaveTeacherFeedback(chatId, state);
                return;
            }

            if (state.Step != "rate" ||
                !int.TryParse(parts[1], out int idx) || idx != state.Index ||
                !int.TryParse(parts[2], out int score) || score < 1 || score > 5)
            {
                await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                return;
            }

            state.Scores[idx] = score;
            state.Index++;
            await bot.AnswerCallbackQuery(cq.Id);

            if (state.Index < TeacherCriteria.Length)
            {
                await AskTeacherFbCriterion(chatId, state);
                return;
            }

            state.Step = "text";
            state.WaitingText = true;
            await Send(chatId, "اگر توضیح بیشتری داری همین‌جا بنویس.\nاگر توضیحی نداری /skip بزن.");
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task HandleTeacherFbText(Message message, TeacherFbState state, string text)
    {
        long chatId = message.Chat.Id;
        bool skip = IsCommand(text, "/skip");

        await state.Gate.WaitAsync();
        try
        {
            if (!state.WaitingText) return;

            if (state.Step == "text")
            {
                state.FreeText = skip ? "" : (text.Length > 1000 ? text.Substring(0, 1000) : text);
                state.WaitingText = false;
                await AskTeacherFbContinue(chatId, state);
                return;
            }

            if (state.Step == "satisfaction")
            {
                if (!int.TryParse(NormalizeDigits(text).Trim(), out int pct) || pct < 0 || pct > 100)
                {
                    await Send(chatId, "لطفاً یک عدد بین ۰ تا ۱۰۰ بنویس.");
                    return;
                }
                state.SatisfactionPercent = pct;
                state.WaitingText = false;
                state.Step = "save";
                await SaveTeacherFeedback(chatId, state);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task SaveTeacherFeedback(long chatId, TeacherFbState state)
    {
        // telegram_id فقط برای کنترل تکراری‌بودن تو سرور (به‌صورت هش) استفاده می‌شه و تو شیت گزارش ذخیره نمی‌شه
        var payload = new Dictionary<string, object>
        {
            ["action"] = "log_teacher_feedback",
            ["telegram_id"] = state.StudentTelegramId,
            ["class_code"] = state.ClassCode,
            ["teacher_id"] = state.TeacherId,
            ["teacher_name"] = state.TeacherName,
            ["free_text"] = state.FreeText,
            ["continue_next_term"] = state.ContinueNextTerm,
            ["satisfaction_percent"] = state.SatisfactionPercent
        };
        for (int i = 0; i < TeacherCriteria.Length; i++)
        {
            var c = TeacherCriteria[i];
            payload[c.Key] = state.Scores[i];                          // عدد 1..5
            payload[c.Key + "_text"] = c.Full[state.Scores[i] - 1];    // برچسب کیفی کامل
        }

        var res = await PostToAppsScriptWithResult(payload);

        if (IsOk(res))
        {
            TeacherFbStates.TryRemove(state.UserId, out _);
            FeedbackGateCache.TryRemove(state.UserId, out _);
            await Send(chatId, "✅ فیدبک شما به‌صورت ناشناس ثبت شد. ممنون از مشارکت شما.");
            return;
        }

        string err = res.HasValue ? JStr(res!.Value, "error") : "";
        if (err == "already_submitted")
        {
            TeacherFbStates.TryRemove(state.UserId, out _);
            FeedbackGateCache.TryRemove(state.UserId, out _);
            await Send(chatId, "فیدبک این ترم را قبلاً ثبت کرده‌ای. ممنون! 🙏");
            return;
        }
        if (err == "not_available_yet")
        {
            TeacherFbStates.TryRemove(state.UserId, out _);
            await Send(chatId, "فیدبک به مدرس بعد از جلسه آخر ترم فعال می‌شود.");
            return;
        }

        // عمداً userId لاگ نمی‌شه (ناشناس بودن)
        AppLog.Error("TeacherFeedback", "log_teacher_feedback failed: " + err);
        await bot.SendMessage(chatId, "❌ ثبت انجام نشد (مشکل سرور). دوباره تلاش کن.",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🔁 تلاش دوباره", "tf|retry|0") }
            }));
    }

    // ----- B) فیدبک مدرس به زبان‌آموز (۲ بار در ترم: بعد از جلسه ۵ و جلسه ۱۰) -----

    static async Task StartStudentFeedback(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /feedback_student را در چت خصوصی بزن.");
            return;
        }
        await StartStudentFeedbackFor(message.From!.Id, chatId);
    }

    static async Task StartStudentFeedbackFor(long userId, long chatId)
    {
        if (!Teachers.TryGetValue(userId, out var classes) || classes.Count == 0)
        {
            await Send(chatId, "این بخش فقط برای مدرس‌هاست.");
            return;
        }

        if (classes.Count == 1)
        {
            await BeginStudentFbClass(userId, chatId, classes[0]);
            return;
        }

        var buttons = classes.Select(c =>
            new[] { InlineKeyboardButton.WithCallbackData(c.ClassCode, "sf|class|" + c.ClassCode) }).ToList();
        await bot.SendMessage(chatId, "فیدبک برای کدام کلاس؟", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task BeginStudentFbClass(long userId, long chatId, TeacherClass tc)
    {
        var students = await LoadStudentsByClass(tc.ClassCode);
        if (students.Count == 0)
        {
            await Send(chatId, "برای کلاس " + tc.ClassCode + " هیچ زبان‌آموزی پیدا نشد.");
            return;
        }

        var state = new StudentFbState
        {
            TeacherId = userId,
            TeacherName = tc.TeacherName,
            ClassCode = tc.ClassCode,
            Students = students
        };

        // ترم فعلی، دور فیدبک و کسانی که فیدبک این دور را گرفته‌اند
        var prog = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = tc.ClassCode });
        if (IsOk(prog) && JBool(prog!.Value, "has_term"))
        {
            var p = prog!.Value;
            state.HasTerm = true;
            state.Term = JStr(p, "term");
            state.Held = (int)(JNum(p, "sessions_held") ?? 0);
            state.Total = (int)(JNum(p, "total_sessions") ?? 0);
            state.Mid = (int)(JNum(p, "midpoint") ?? 0);
            int due = (int)(JNum(p, "feedback_round_due") ?? 0);
            state.Round = due > 0 ? due : 1;
            state.Early = due == 0;

            if (p.TryGetProperty("done_student_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                foreach (var x in ids.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) state.DoneIds.Add(x.GetString() ?? "");
        }

        state.Phase = state.Round == 2 ? "Final" : "Mid";
        StudentFbStates[userId] = state;

        // وقتی هر دو دور قابل ثبت است، مدرس باید صریحاً Mid یا Final را انتخاب کند.
        if (state.HasTerm && state.Mid > 0 && state.Held >= state.Mid)
        {
            await AskStudentFbPhase(chatId, state);
            return;
        }

        await AskStudentFbStudent(chatId, state);
    }

    static async Task AskStudentFbPhase(long chatId, StudentFbState state)
    {
        state.Step = "phase";
        string finalLabel = "Final (جلسه " + state.Total + ")";
        if (state.Total > 0 && state.Held < state.Total) finalLabel += " — زودهنگام";
        await bot.SendMessage(chatId,
            "📋 فیدبک به زبان‌آموز — کلاس " + state.ClassCode + "\n" +
            "دور فیدبک را انتخاب کن تا نمرات Mid و Final جدا بمانند:",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Mid (جلسه " + state.Mid + ")", "sf|phase|mid"),
                    InlineKeyboardButton.WithCallbackData(finalLabel, "sf|phase|final")
                }
            }));
    }

    static async Task LoadStudentFbDone(StudentFbState state, int round)
    {
        state.DoneIds.Clear();
        var prog = await PostToAppsScriptWithResult(new
        {
            action = "term_progress",
            class_code = state.ClassCode,
            round = round
        });
        if (IsOk(prog) && prog!.Value.TryGetProperty("done_student_ids", out var ids) &&
            ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in ids.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) state.DoneIds.Add(x.GetString() ?? "");
        }
    }

    static async Task AskStudentFbStudent(long chatId, StudentFbState state)
    {
        state.Step = "student";
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < state.Students.Count; i += 2)
        {
            var row = new List<InlineKeyboardButton>();
            for (int j = i; j < Math.Min(i + 2, state.Students.Count); j++)
            {
                var s = state.Students[j];
                string name = string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name;
                string label = state.DoneIds.Contains(s.StudentId) ? "✓ " + name : name;
                row.Add(InlineKeyboardButton.WithCallbackData(label, "sf|stu|" + j));
            }
            rows.Add(row.ToArray());
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ پایان", "sf|done|0") });

        var sb = new StringBuilder();
        sb.Append("📋 فیدبک به زبان‌آموز — کلاس " + state.ClassCode + "\n");
        if (state.HasTerm)
        {
            sb.Append("ترم: " + state.Term + "  ·  جلسه " + state.Held + " از " + state.Total + "\n");
            sb.Append("دور فیدبک: " + state.Round + (state.Round == 1 ? " (بعد از جلسه " + state.Mid + ")" : " (بعد از جلسه " + state.Total + ")") + "\n");
            if (state.Early) sb.Append("⚠️ هنوز به جلسه " + state.Mid + " نرسیده‌ای؛ این فیدبک زودهنگام ثبت می‌شود.\n");
        }
        else
        {
            sb.Append("ℹ️ ترمی برای این کلاس شروع نشده (تو /classdone دکمه «شروع ترم» را بزن).\n");
        }
        sb.Append("\nزبان‌آموز را انتخاب کن (✓ = فیدبک این دور ثبت شده):");

        await bot.SendMessage(chatId, sb.ToString(), replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task AskStudentFbCriterion(long chatId, StudentFbState state)
    {
        var c = StudentCriteria[state.Index];
        await bot.SendMessage(chatId,
            BuildCriterionText("📋", state.Index, StudentCriteria.Length, c, state.StudentName),
            replyMarkup: BuildScoreKeyboard("sf|r|" + state.Index, c));
    }

    static async Task HandleStudentFbCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3) { await bot.AnswerCallbackQuery(cq.Id); return; }
        string action = parts[1];
        string value = parts[2];

        if (!Teachers.TryGetValue(userId, out var myClasses))
        {
            await bot.AnswerCallbackQuery(cq.Id, "این بخش فقط برای مدرس‌هاست.");
            return;
        }

        if (action == "start")
        {
            await bot.AnswerCallbackQuery(cq.Id);
            await StartStudentFeedbackFor(userId, chatId);
            return;
        }

        if (action == "class")
        {
            var tc = myClasses.FirstOrDefault(x => x.ClassCode == value);
            await bot.AnswerCallbackQuery(cq.Id);
            if (tc != null) await BeginStudentFbClass(userId, chatId, tc);
            return;
        }

        if (!StudentFbStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /feedback_student بزن.");
            return;
        }

        await state.Gate.WaitAsync();
        try
        {
            if (action == "phase")
            {
                if (state.Step != "phase" || (value != "mid" && value != "final"))
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }
                state.Round = value == "final" ? 2 : 1;
                state.Phase = value == "final" ? "Final" : "Mid";
                state.Early = false;
                state.Step = "student";
                await bot.AnswerCallbackQuery(cq.Id);
                await LoadStudentFbDone(state, state.Round);
                await AskStudentFbStudent(chatId, state);
                return;
            }

            if (action == "stu")
            {
                if (state.Step != "student" || !int.TryParse(value, out int si) || si < 0 || si >= state.Students.Count)
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }

                var st = state.Students[si];
                state.StudentId = st.StudentId;
                state.StudentName = string.IsNullOrWhiteSpace(st.Name) ? st.StudentId : st.Name;
                state.Index = 0;
                state.Scores = new int[StudentCriteria.Length];
                state.Strengths = "";
                state.FreeText = "";
                state.Step = "rate";
                await bot.AnswerCallbackQuery(cq.Id);
                await AskStudentFbCriterion(chatId, state);
                return;
            }

            if (action == "r")
            {
                if (parts.Length != 4 || state.Step != "rate" ||
                    !int.TryParse(value, out int idx) || idx != state.Index ||
                    !int.TryParse(parts[3], out int score) || score < 1 || score > 5)
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }

                state.Scores[idx] = score;
                state.Index++;
                await bot.AnswerCallbackQuery(cq.Id);

                if (state.Index < StudentCriteria.Length)
                {
                    await AskStudentFbCriterion(chatId, state);
                    return;
                }

                state.Step = "strengths";
                state.WaitingText = true;
                await Send(chatId, "نقاط قوت زبان‌آموز را بنویس (متن آزاد).\nاگر موردی نیست /skip بزن.");
                return;
            }

            if (action == "retry")
            {
                if (state.Step != "save") { await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه."); return; }
                await bot.AnswerCallbackQuery(cq.Id);
                await SaveStudentFeedback(chatId, state);
                return;
            }

            if (action == "done")
            {
                await bot.AnswerCallbackQuery(cq.Id);
                StudentFbStates.TryRemove(userId, out _);
                await Send(chatId, "✅ ثبت فیدبک‌ها تمام شد. تعداد ثبت‌شده برای دور " + state.Round + ": " + state.DoneIds.Count);
                return;
            }

            await bot.AnswerCallbackQuery(cq.Id);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task HandleStudentFbText(Message message, StudentFbState state, string text)
    {
        long chatId = message.Chat.Id;
        bool skip = IsCommand(text, "/skip");

        await state.Gate.WaitAsync();
        try
        {
            if (!state.WaitingText) return;
            string value = skip ? "" : (text.Length > 1000 ? text.Substring(0, 1000) : text);

            if (state.Step == "strengths")
            {
                state.Strengths = value;
                state.Step = "free";
                await Send(chatId, "اگر توضیح نهایی دیگری داری بنویس.\nدر غیر این صورت /skip بزن.");
                return;
            }

            if (state.Step == "free")
            {
                state.FreeText = value;
                state.WaitingText = false;
                state.Step = "save";
                await SaveStudentFeedback(chatId, state);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task SaveStudentFeedback(long chatId, StudentFbState state)
    {
        var payload = new Dictionary<string, object>
        {
            ["action"] = "log_student_feedback",
            ["class_code"] = state.ClassCode,
            ["student_id"] = state.StudentId,
            ["student_name"] = state.StudentName,
            ["teacher_id"] = state.TeacherId,
            ["teacher_name"] = state.TeacherName,
            ["term"] = string.IsNullOrWhiteSpace(state.Term) ? TermFor(state.ClassCode) : state.Term,
            ["term_hint"] = TermFor(state.ClassCode),
            ["round"] = state.Round,
            ["phase"] = state.Phase,
            ["feedback_session"] = state.Round == 2 ? state.Total : state.Mid,
            ["strengths"] = state.Strengths,
            ["free_text"] = state.FreeText
        };
        for (int i = 0; i < StudentCriteria.Length; i++)
        {
            var c = StudentCriteria[i];
            payload[c.Key] = state.Scores[i];                          // عدد 1..5
            payload[c.Key + "_text"] = c.Full[state.Scores[i] - 1];    // برچسب کیفی کامل
        }

        var (ok, err) = await PostToAppsScriptWithError(payload);

        if (!ok)
        {
            AppLog.Error("StudentFeedback", "log_student_feedback failed: " + err, null, state.TeacherId);
            await bot.SendMessage(chatId, "❌ ذخیره فیدبک انجام نشد (مشکل سرور).",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔁 تلاش دوباره", "sf|retry|0") }
                }));
            return;
        }

        state.DoneIds.Add(state.StudentId);
        await Send(chatId, "✅ فیدبک برای " + state.StudentName + " ثبت شد.");
        RecomputeInBackground(state.ClassCode, "StudentFeedback.Recompute");
        await AskStudentFbStudent(chatId, state);
    }

    // ===================== TERM (شروع و پایان ترم، یادآوری، دروازه فیدبک) =====================

    // قاعده آکادمی: سطح ۱۰۱..۶۰۶ = کتاب (رقم اول) + زیرسطح (رقم دوم).
    // هر زیرسطح ۲ یونیت Evolve را پوشش می‌دهد: unitA=(sub-1)*2+1 و unitB=unitA+1.
    // مثال: 204 -> کتاب Evolve 2، یونیت‌های 7 و 8 (۶ زیرسطح × ۲ یونیت = ۱۲ یونیت هر کتاب).
    static (int Book, int Unit1, int Unit2) MapSubLevelToUnits(string subLevel)
    {
        if (!int.TryParse((subLevel ?? "").Trim(), out int lvl) || lvl < 101 || lvl > 606) return (0, 0, 0);
        int book = lvl / 100;
        int sub = lvl % 100;
        if (book < 1 || book > 6 || sub < 1 || sub > 6) return (0, 0, 0);
        int unit1 = (sub - 1) * 2 + 1;
        return (book, unit1, unit1 + 1);
    }

    static List<string> TermSessions(int unit1, int unit2)
    {
        var list = new List<string>();
        for (int i = 1; i <= LessonsPerUnit; i++) list.Add(unit1 + "_" + i);
        for (int i = 1; i <= LessonsPerUnit; i++) list.Add(unit2 + "_" + i);
        return list;
    }

    static async Task<TermStructure?> GetTermStructure(string classCode)
    {
        var res = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = classCode });
        if (!IsOk(res)) return null;
        var r = res!.Value;
        if (!JBool(r, "has_term")) return null;

        var ts = new TermStructure
        {
            HasTerm = true,
            Active = JStr(r, "status") == "Active",
            Term = JStr(r, "term"),
            Book = (int)(JNum(r, "book") ?? 0),
            Unit1 = JStr(r, "unit1"),
            Unit2 = JStr(r, "unit2"),
            Level = JStr(r, "level"),
            StartDate = JStr(r, "start_date"),
            SessionsHeld = (int)(JNum(r, "sessions_held") ?? 0),
            TotalSessions = (int)(JNum(r, "total_sessions") ?? 0)
        };
        if (r.TryGetProperty("sessions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                string ul = s.ValueKind == JsonValueKind.String ? (s.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(ul)) ts.Sessions.Add(ul.Trim());
            }
        }
        if (ts.Sessions.Count == 0 &&
            int.TryParse(ts.Unit1, out int u1) && int.TryParse(ts.Unit2, out int u2))
            ts.Sessions = TermSessions(u1, u2);
        return ts;
    }

    static string TermLine(TermStructure? ts)
    {
        if (ts == null)
            return "ℹ️ هنوز ترمی برای این کلاس شروع نشده. برای شروع از دکمه «🟢 شروع ترم جدید» استفاده کن.\n";
        if (!ts.Active)
            return "ℹ️ ترم قبلی (" + ts.Term + ") پایان یافته. برای ترم تازه «🟢 شروع ترم جدید» را بزن.\n";

        string line = "📆 ترم فعال: " + ts.Term + " — جلسه " + ts.SessionsHeld + " از " + ts.TotalSessions + "\n";
        if (ts.Unit1.Length > 0 && ts.Unit2.Length > 0)
            line += "📚 Evolve " + ts.Book + " | یونیت " + ts.Unit1 + " و " + ts.Unit2 + "\n";
        return line;
    }

    static InlineKeyboardMarkup TermLevelButtons()
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 1; i <= 6; i += 3)
            rows.Add(Enumerable.Range(i, 3)
                .Select(n => InlineKeyboardButton.WithCallbackData("Level " + n, "cd|tset|lvl|" + n)).ToArray());
        return new InlineKeyboardMarkup(rows);
    }

    static InlineKeyboardMarkup TermSubLevelButtons(int level)
    {
        var rows = new List<InlineKeyboardButton[]>();
        var buttons = Enumerable.Range(1, 6)
            .Select(n => InlineKeyboardButton.WithCallbackData((level * 100 + n).ToString(), "cd|tset|sub|" + (level * 100 + n)))
            .ToList();
        for (int i = 0; i < buttons.Count; i += 3)
            rows.Add(buttons.Skip(i).Take(3).ToArray());
        return new InlineKeyboardMarkup(rows);
    }

    static InlineKeyboardMarkup TermWeekdayButtons(string selected)
    {
        var picked = (selected ?? "").Split(new[] { '،', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var day in PersianWeekdays)
        {
            string label = (picked.Contains(day) ? "✅ " : "") + day;
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, "cd|tset|day|" + day) });
        }
        // چند انتخاب روز + یک دکمه تأیید؛ کلیک روی روزها state را پاک نمی‌کند.
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ تأیید روزها", "cd|tset|daysdone") });
        return new InlineKeyboardMarkup(rows);
    }

    static readonly string[] TermTimeSlots =
    {
        "08:00-10:00", "10:00-12:00", "14:00-16:00",
        "16:00-18:00", "18:00-20:00", "20:00-22:00"
    };

    static InlineKeyboardMarkup TermTimeButtons()
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < TermTimeSlots.Length; i += 2)
            rows.Add(TermTimeSlots.Skip(i).Take(2)
                .Select(t => InlineKeyboardButton.WithCallbackData(t, "cd|tset|time|" + t)).ToArray());
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✍️ ورود دستی بازه زمانی", "cd|tset|timecustom") });
        return new InlineKeyboardMarkup(rows);
    }

    static List<string> TermSelectedDays(ClassDoneState state)
    {
        return (state.TermWeekdays ?? "")
            .Split(new[] { '،', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    // برای هر روز انتخاب‌شده، بازه زمانی همان روز را جدا می‌پرسد.
    static async Task PromptTermDayTime(long chatId, ClassDoneState state)
    {
        var days = TermSelectedDays(state);
        if (state.TermDayIndex >= days.Count)
        {
            state.TermSetupStep = "";
            await StartTermWithInfo(chatId, state);
            return;
        }

        state.TermSetupStep = "daytime";
        state.WaitingText = false;
        state.TextMode = "";
        string day = days[state.TermDayIndex];
        await bot.SendMessage(chatId,
            "⏰ بازه زمانی " + day + " را انتخاب کن، یا «✍️ ورود دستی بازه زمانی» را بزن:",
            replyMarkup: TermTimeButtons());
    }

    static async Task SaveTermDayTimeAndContinue(long chatId, ClassDoneState state, string start, string end)
    {
        var days = TermSelectedDays(state);
        if (state.TermDayIndex < days.Count)
            state.TermDayTimes[days[state.TermDayIndex]] = start + "-" + end;
        state.TermDayIndex++;
        await PromptTermDayTime(chatId, state);
    }

    // بازه زمانی «شروع تا پایان» را از متن آزاد می‌خواند: «16:30-18:00»، «۱۶:۳۰ تا ۱۸:۰۰»، «16.30-18».
    // ارقام فارسی/عربی و جداکننده‌های - – — تا را قبول می‌کند و خروجی نرمال‌شده HH:mm می‌دهد.
    static bool TryParseTimeRange(string? text, out string start, out string end)
    {
        start = ""; end = "";
        string s = NormalizeDigits(text ?? "").Replace('–', '-').Replace('—', '-').Replace('.', ':');
        var m = Regex.Match(s, @"(\d{1,2}):(\d{2})\s*(?:-|تا|to)\s*(\d{1,2}):(\d{2})",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        int sh = int.Parse(m.Groups[1].Value), sm = int.Parse(m.Groups[2].Value);
        int eh = int.Parse(m.Groups[3].Value), em = int.Parse(m.Groups[4].Value);
        if (sh > 23 || sm > 59 || eh > 23 || em > 59) return false;
        start = sh.ToString("00", CultureInfo.InvariantCulture) + ":" + sm.ToString("00", CultureInfo.InvariantCulture);
        end = eh.ToString("00", CultureInfo.InvariantCulture) + ":" + em.ToString("00", CultureInfo.InvariantCulture);
        return true;
    }

    // ارقام فارسی/عربی را به ASCII تبدیل می‌کند تا پارس زمان/عدد ساده شود.
    static string NormalizeDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '\u06F0' && c <= '\u06F9') sb.Append((char)('0' + (c - '\u06F0')));       // ۰-۹
            else if (c >= '\u0660' && c <= '\u0669') sb.Append((char)('0' + (c - '\u0660')));  // ٠-٩
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static InlineKeyboardMarkup YesNoButtons(string yesData)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ بله", "cd|term|" + yesData),
                InlineKeyboardButton.WithCallbackData("❌ خیر", "cd|term|no"),
            }
        });
    }

    static async Task HandleTermButton(long chatId, ClassDoneState state, string value)
    {
        if (value == "no")
        {
            await Send(chatId, "لغو شد.");
            return;
        }

        if (value == "start")
        {
            await bot.SendMessage(chatId,
                "ترم جدید برای کلاس " + state.ClassCode + " از امروز (" + state.Date + ") شروع شود؟\n" +
                "نتیجه ترم و فیدبک‌ها از این تاریخ حساب می‌شوند. زبان‌آموزانی که فیدبک ترم قبل را به مدرس نداده باشند، تا ثبت آن نمی‌توانند از بات استفاده کنند.",
                replyMarkup: YesNoButtons("ystart"));
            return;
        }

        if (value == "ystart")
        {
            state.TermSetupStep = "classtype";
            state.TermClassType = "";
            state.TermPackage = "";
            state.TermWeekdays = "";
            state.TermStartTime = "";
            state.TermEndTime = "";
            state.TermStartDate = "";
            state.TermEndDate = "";
            state.TermDayTimes.Clear();
            state.TermDayIndex = 0;
            state.TermLevel = state.Level;
            state.TermBook = "";
            state.TermUnit1 = "";
            state.TermUnit2 = "";

            await bot.SendMessage(chatId, "نوع کلاس این ترم چیست؟",
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("خصوصی", "cd|tset|ctype|private"),
                        InlineKeyboardButton.WithCallbackData("عمومی", "cd|tset|ctype|public")
                    }
                }));
            return;
        }

        if (value == "package")
        {
            var packages = new[] { "کتاب‌محور", "Talk", "Story Mode", "Discussion" };
            var rows = packages.Select(p => new[]
            {
                InlineKeyboardButton.WithCallbackData(p, "cd|tset|pkg|" + p)
            }).ToList();
            await bot.SendMessage(chatId, "نوع پکیج دوره این ترم چیست؟",
                replyMarkup: new InlineKeyboardMarkup(rows));
            return;
        }

        if (value == "end")
        {
            var prog = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = state.ClassCode });
            if (!IsOk(prog) || !JBool(prog!.Value, "has_term") || JStr(prog!.Value, "status") != "Active")
            {
                await Send(chatId, "ترم فعالی برای پایان دادن وجود ندارد.");
                return;
            }

            var p = prog!.Value;
            state.TermSetupStep = "endtermdate";
            state.WaitingText = false;
            state.TextMode = "";
            await Send(chatId,
                "ترم «" + JStr(p, "term") + "» (از " + JStr(p, "start_date") + ")\n" +
                "📅 تاریخ پایان ترم را بنویس (مثلاً 2026-05-20).\nبرای لغو: /cancel");
            return;
        }

        if (value == "yend")
        {
            await FinishTerm(chatId, state, state.TermEndDate);
        }
    }

    static async Task FinishTerm(long chatId, ClassDoneState state, string endDate)
    {
        var res = await PostToAppsScriptWithResult(new
        {
            action = "end_term",
            class_code = state.ClassCode,
            teacher_name = state.TeacherName,
            end_date = endDate
        });

        if (!IsOk(res))
        {
            string err = res.HasValue ? JStr(res!.Value, "error") : "";
            if (err == "no_active_term")
            {
                await Send(chatId, "ترم فعالی برای پایان دادن وجود ندارد.");
                return;
            }
            if (err == "end_before_start")
            {
                await Send(chatId, "تاریخ پایان نباید قبل از تاریخ شروع ترم باشد. دوباره «🔴 پایان ترم» را بزن.");
                return;
            }
            AppLog.Error("Term", "end_term failed: " + err, null, state.TeacherTelegramId);
            await Send(chatId, "❌ پایان ترم انجام نشد: " + (err == "" ? "ارتباط با سرور برقرار نشد" : err));
            return;
        }

        AppLog.Info("Term", "ended term for class " + state.ClassCode, state.TeacherTelegramId);
        await Send(chatId,
            "🏁 ترم پایان یافت.\n" +
            "نام ترم: " + JStr(res!.Value, "term") + "\n" +
            "بازه: " + JStr(res!.Value, "start_date") + " تا " + JStr(res!.Value, "end_date") + "\n" +
            "جلسات برگزارشده: " + JStr(res!.Value, "sessions_held") + " از " + JStr(res!.Value, "total_sessions") + "\n\n" +
            "نتایج نهایی ترم محاسبه شد: /classresults");

        await NotifyLearnersTeacherFeedback(chatId, state.ClassCode);
    }

    static async Task HandleTermSetupText(Message message, ClassDoneState state, string text)
    {
        long chatId = message.Chat.Id;

        // ورود دستی بازه زمانی کلاس (به‌جای تکیه صرف روی دکمه‌های آماده)
        if (state.TermSetupStep == "startdate")
        {
            var d = ParseDate(NormalizeDigits(text));
            if (d == null)
            {
                await Send(chatId, "تاریخ شروع نامعتبر است. مثلاً: 2026-03-01\nبرای لغو: /cancel");
                return;
            }
            state.TermStartDate = d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            state.TermDayIndex = 0;
            await PromptTermDayTime(chatId, state);
            return;
        }

        if (state.TermSetupStep == "endtermdate")
        {
            var d = ParseDate(NormalizeDigits(text));
            if (d == null)
            {
                await Send(chatId, "تاریخ پایان نامعتبر است. مثلاً: 2026-05-20\nبرای لغو: /cancel");
                return;
            }
            state.TermEndDate = d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            state.TermSetupStep = "";
            await FinishTerm(chatId, state, state.TermEndDate);
            return;
        }

        if (state.TermSetupStep == "daytime" && (state.WaitingText || state.TextMode == "termtime"))
        {
            if (!TryParseTimeRange(text, out string tStart, out string tEnd))
            {
                await Send(chatId, "زمان نامعتبر است. مثلاً: 16:30-18:00 یا ۱۶:۳۰ تا ۱۸:۰۰\nبرای لغو: /cancel");
                return;
            }
            state.WaitingText = false;
            state.TextMode = "";
            await SaveTermDayTimeAndContinue(chatId, state, tStart, tEnd);
            return;
        }

        await Send(chatId, "برای شروع ترم از دکمه‌های همین پیام استفاده کن.");
    }

    static async Task StartTermWithInfo(long chatId, ClassDoneState state)
    {
        var days = TermSelectedDays(state);
        var dayRanges = new Dictionary<string, (string Start, string End)>();
        foreach (var day in days)
        {
            if (state.TermDayTimes.TryGetValue(day, out var rng) &&
                TryParseTimeRange(rng, out string ds, out string de))
                dayRanges[day] = (ds, de);
            else if (!string.IsNullOrWhiteSpace(state.TermStartTime))
                dayRanges[day] = (state.TermStartTime, state.TermEndTime);
        }
        string firstStart = state.TermStartTime;
        string firstEnd = state.TermEndTime;
        if (days.Count > 0 && dayRanges.TryGetValue(days[0], out var firstRange))
        {
            firstStart = firstRange.Start;
            firstEnd = firstRange.End;
        }

        var res = await PostToAppsScriptWithResult(new
        {
            action = "start_term",
            class_code = state.ClassCode,
            teacher_name = state.TeacherName,
            class_type = state.TermClassType,
            course_package = state.TermPackage,
            weekdays = state.TermWeekdays,
            start_date = state.TermStartDate,
            start_time = firstStart,
            end_time = firstEnd,
            level = state.TermLevel,
            book = state.TermBook,
            unit1 = state.TermUnit1,
            unit2 = state.TermUnit2,
            sessions = string.Join(",", TermSessions(
                int.TryParse(state.TermUnit1, out int tu1) ? tu1 : 0,
                int.TryParse(state.TermUnit2, out int tu2) ? tu2 : 0))
        });

        if (IsOk(res))
        {
            FeedbackGateCache.Clear();
            AppLog.Info("Term", "started term for class " + state.ClassCode +
                " package=" + state.TermPackage + " units=" + state.TermUnit1 + "," + state.TermUnit2,
                state.TeacherTelegramId);
            var dayLines = string.Join("\n", days.Select(d =>
                "• " + d + ": " + (dayRanges.TryGetValue(d, out var r)
                    ? r.Start + " تا " + r.End
                    : "—")));
            await Send(chatId,
                "✅ ترم جدید شروع شد.\n" +
                "نام ترم: " + JStr(res!.Value, "term") + "\n" +
                "تاریخ شروع: " + JStr(res!.Value, "start_date") + "\n" +
                "تعداد جلسات ترم: " + JStr(res!.Value, "total_sessions") + "\n" +
                "نوع کلاس: " + (state.TermClassType == "private" ? "خصوصی" : "عمومی") + "\n" +
                "پکیج: " + state.TermPackage + "\n" +
                "روزها و ساعت‌ها:\n" + dayLines + "\n" +
                "سطح: " + state.TermLevel + " | Evolve " + state.TermBook + " | یونیت‌ها: " + state.TermUnit1 + " و " + state.TermUnit2 + "\n" +
                "جلسات: " + string.Join("، ", TermSessions(
                    int.TryParse(state.TermUnit1, out int su1) ? su1 : 0,
                    int.TryParse(state.TermUnit2, out int su2) ? su2 : 0)) + "\n");

            // Class_Schedule با بازه زمانی مستقل هر روز پر می‌شود.
            foreach (var day in days)
            {
                if (!dayRanges.TryGetValue(day, out var range)) continue;
                var (schedOk, schedErr) = await PostToAppsScriptWithError(new
                {
                    action = "save_class_schedule",
                    class_code = state.ClassCode,
                    weekday = day,
                    start_time = range.Start,
                    end_time = range.End,
                    teacher_id = state.TeacherTelegramId.ToString(CultureInfo.InvariantCulture),
                    teacher_name = state.TeacherName
                });
                if (!schedOk)
                    AppLog.Warn("Term", "save_class_schedule failed for " + state.ClassCode + "/" + day + ": " + schedErr,
                        state.TeacherTelegramId);
            }

            // سطح/ترم ثبت‌شده از «شروع ترم» در رجیستری هم آینه می‌شود.
            var reg = RegisterClass(state.ClassCode, 0, state.TermLevel, JStr(res!.Value, "term"),
                state.TeacherTelegramId, state.TeacherName, true, "term");
            if (reg != null) await PushClassRegistry(reg);

            // همه زبان‌آموزان شناخته‌شده همین کلاس سطح/یونیت ترم جدید را در Student_Info بگیرند
            foreach (var gs in GroupStudents.Values.Where(s => Eq(s.ClassCode, state.ClassCode)))
            {
                gs.Level = state.TermLevel;
                MirrorStudentLevel(gs.StudentId, state.ClassCode, state.TermLevel, state.TermUnit1, "1");
            }

            await BeginClassSession(state.TeacherTelegramId, chatId, new TeacherClass
            {
                ClassCode = state.ClassCode,
                TeacherName = state.TeacherName,
                Level = state.TermLevel
            });
            return;
        }

        string err = res.HasValue ? JStr(res!.Value, "error") : "";
        if (err == "term_already_active")
        {
            await Send(chatId, "ترم قبلی (" + JStr(res!.Value, "term") + "، از " + JStr(res!.Value, "start_date") +
                ") هنوز فعال است. اول «🔴 پایان ترم» را بزن.");
            return;
        }

        AppLog.Error("Term", "start_term failed: " + err, null, state.TeacherTelegramId);
        await Send(chatId, "❌ شروع ترم انجام نشد: " + (err == "" ? "ارتباط با سرور برقرار نشد" : err));
    }

    // یادآوری بعد از جلسه ۵ (دور ۱) و جلسه آخر (دور ۲) برای فیدبک مدرس به زبان‌آموزان؛
    // بعد از جلسه آخر، از زبان‌آموزان هم خواسته می‌شود فیدبک ناشناس به مدرس بدهند.
    static async Task AfterSessionTermChecks(long chatId, ClassDoneState state)
    {
        try
        {
            var res = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = state.ClassCode });
            if (!IsOk(res) || !JBool(res!.Value, "has_term")) return;

            var r = res!.Value;
            if (JStr(r, "status") != "Active") return;

            int held = (int)(JNum(r, "sessions_held") ?? 0);
            int total = (int)(JNum(r, "total_sessions") ?? 0);
            int mid = (int)(JNum(r, "midpoint") ?? 0);
            int pending = (int)(JNum(r, "pending_count") ?? 0);
            var doneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("done_student_ids", out var doneArr) &&
                doneArr.ValueKind == JsonValueKind.Array)
                foreach (var x in doneArr.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) doneIds.Add(x.GetString() ?? "");

            bool midNow = mid > 0 && held == mid;
            bool endNow = total > 0 && held == total;

            if (midNow || endNow)
            {
                int round = endNow ? 2 : 1;
                string phase = round == 2 ? "Final" : "Mid";
                var pendingNames = state.Students
                    .Where(s => !doneIds.Contains(s.StudentId))
                    .Select(s => string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name)
                    .ToList();
                string pendingLine = pendingNames.Count > 0
                    ? "زبان‌آموزان بدون فیدبک: " + string.Join("، ", pendingNames)
                    : "همه زبان‌آموزان این دور ثبت شده‌اند.";
                var kb = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📋 شروع فیدبک به زبان‌آموزان", "sf|start|0") }
                });
                await bot.SendMessage(chatId,
                    "🔔 یادآوری فیدبک (" + phase + " / دور " + round + ")\n" +
                    "جلسه " + held + " از " + total + " ثبت شد؛ وقت فیدبک به زبان‌آموزهاست.\n" +
                    "هنوز برای " + pending + " زبان‌آموز فیدبک این دور ثبت نشده.\n" +
                    pendingLine + "\n\n" +
                    "دستور: /feedback_student",
                    replyMarkup: kb);
            }

            if (endNow)
            {
                await Send(chatId, "🏁 جلسات این ترم به " + total + " رسید. برای بستن ترم، تو /classdone دکمه «🔴 پایان ترم» را بزن.");
                await NotifyLearnersTeacherFeedback(chatId, state.ClassCode);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("ClassDone.TermChecks", "failed", ex, state.TeacherTelegramId);
        }
    }

    // یادآوری به زبان‌آموزان: فیدبک ناشناس به مدرس (فقط یک بار برای هر ترم)
    static async Task NotifyLearnersTeacherFeedback(long teacherChatId, string classCode)
    {
        var prog = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = classCode });
        if (IsOk(prog) && JBool(prog!.Value, "learners_notified")) return;

        var students = await LoadStudentsByClass(classCode);
        int okCount = 0, failCount = 0;
        foreach (var s in students)
        {
            if (!long.TryParse(s.StudentId, out long sid)) continue;
            try
            {
                await bot.SendMessage(sid,
                    "⭐ ترم تمام شد!\n\n" +
                    "لطفاً فیدبک ناشناس خودت درباره مدرس را ثبت کن: /feedback_teacher\n\n" +
                    "ثبت این فیدبک برای استفاده از بات در ترم بعد الزامی است.");
                okCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                AppLog.Warn("FeedbackNotify", "send failed: " + ex.Message, sid);
            }
            await Task.Delay(100);
        }

        await PostToAppsScript(new { action = "mark_feedback_notified", class_code = classCode });
        await Send(teacherChatId,
            "📨 یادآوری فیدبک به مدرس برای " + okCount + " زبان‌آموز ارسال شد" +
            (failCount > 0 ? " (" + failCount + " نفر هنوز بات را Start نکرده‌اند)" : "") + ".");
    }

    // دروازه: زبان‌آموزی که ترم قبل را تمام کرده و فیدبک ناشناس به مدرس نداده، تو ترم جدید از بات استفاده نمی‌کنه
    private static readonly ConcurrentDictionary<long, (bool Required, DateTime At)> FeedbackGateCache = new();

    static async Task<bool> BlockedByFeedbackGate(long chatId, long userId)
    {
        bool required;
        if (FeedbackGateCache.TryGetValue(userId, out var cached) &&
            (DateTime.UtcNow - cached.At).TotalMinutes < (cached.Required ? 1 : 10))
        {
            required = cached.Required;
        }
        else
        {
            var res = await PostToAppsScriptWithResult(new { action = "check_feedback_required", student_id = userId.ToString() });
            required = IsOk(res) && JBool(res!.Value, "required");   // اگر سرور جواب نداد، مسدود نمی‌کنیم
            FeedbackGateCache[userId] = (required, DateTime.UtcNow);
        }

        if (required)
            await Send(chatId,
                "⛔ برای استفاده از بات در ترم جدید، اول باید فیدبک ناشناس ترم قبل را به مدرس ثبت کنی.\n\n/feedback_teacher");
        return required;
    }

    // ===================== TERM RESULTS (نتیجه ترم) =====================

    static double? JNum(JsonElement e, string name)
    {
        string s = JStr(e, name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    }

    static string Fmt(JsonElement e, string name, string suffix)
    {
        var n = JNum(e, name);
        return n.HasValue ? n.Value.ToString("0.#", CultureInfo.InvariantCulture) + suffix : "—";
    }

    static string ResultGuidance(JsonElement r)
    {
        string status = JStr(r, "status");
        if (status == "Pass") return "آفرین! همین روند رو ادامه بده. 👏";
        if (status == "In Progress") return "ترم هنوز تموم نشده و این نتیجه موقتیه. همین روند رو تا آخر ترم حفظ کن. 💪";
        if (status == "Incomplete") return "هنوز داده کافی برای محاسبه کامل نتیجه وجود نداره. با مدرس یا آموزشگاه هماهنگ کن.";

        var comps = new List<(string Name, double Value)>();
        var att = JNum(r, "attendance_percent");
        if (att.HasValue) comps.Add(("حضور منظم در کلاس", att.Value));
        var pr = JNum(r, "practice_percent");
        if (pr.HasValue) comps.Add(("تمرین روزانه", pr.Value));
        var hw = JNum(r, "homework_percent");
        if (hw.HasValue) comps.Add(("تحویل تکلیف", hw.Value));
        var te = JNum(r, "teacher_score_100");
        if (te.HasValue) comps.Add(("مشارکت و عملکرد در کلاس", te.Value));

        if (comps.Count == 0) return "";
        var weakest = comps.OrderBy(c => c.Value).Take(2).Select(c => c.Name).ToList();
        var heldNow = JNum(r, "sessions_held");
        var finalAfter = JNum(r, "final_after_sessions");
        bool provisional = heldNow.HasValue && finalAfter.HasValue && heldNow.Value < finalAfter.Value;
        return "برای بهبود روی " + string.Join(" و ", weakest) + " تمرکز کن." +
               (provisional ? "\n(این هشدار موقتیه؛ ترم هنوز تموم نشده.)" : "");
    }

    static string FormatStudentResult(JsonElement r, string termFallback = "")
    {
        var sb = new StringBuilder();
        string termLabel = JStr(r, "term");
        if (termLabel == "") termLabel = termFallback == "" ? ActiveTermId : termFallback;
        sb.Append("📊 نتیجه ترم\n");
        sb.Append("ترم: " + termLabel + "\n");
        sb.Append("وضعیت: " + JStr(r, "status") + "\n");
        if (JNum(r, "sessions_held").HasValue && JNum(r, "total_sessions").HasValue)
            sb.Append("جلسات برگزارشده: " + Fmt(r, "sessions_held", "") + " از " + Fmt(r, "total_sessions", "") + "\n");
        sb.Append("نمره نهایی: " + Fmt(r, "final_score", "") + "\n");
        sb.Append("حضور: " + Fmt(r, "attendance_percent", "%") + "\n");
        sb.Append("تمرین: " + Fmt(r, "practice_percent", "%") + "\n");
        sb.Append("تکلیف: " + Fmt(r, "homework_percent", "%") + "\n");
        sb.Append("ارزیابی مدرس: " + Fmt(r, "teacher_score_100", "") + "\n\n");

        string guide = ResultGuidance(r);
        if (guide != "") sb.Append(guide + "\n\n");

        sb.Append("ℹ️ این نتیجه «پیشنهاد سیستمی» است و نتیجه نهایی با تأیید آموزشگاه مشخص می‌شود.");
        return sb.ToString();
    }

    static async Task StartMyResult(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /myresult را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        var student = await FindStudentFull(userId.ToString());
        if (student == null)
        {
            await Send(chatId, "اطلاعات تو تو Student_Info پیدا نشد. با آموزشگاه هماهنگ کن.");
            return;
        }

        // نتیجه‌ی همین ترم را قبل از نمایش تازه می‌کنیم.
        await RecomputeForClass(student.ClassCode, "MyResult.Recompute", force: true);

        var res = await PostToAppsScriptWithResult(new
        {
            action = "get_student_term_result",
            student_id = student.StudentId,
            class_code = student.ClassCode,
            term_hint = TermFor(student.ClassCode)
        });

        if (!res.HasValue)
        {
            await Send(chatId, "ارتباط با سرور برقرار نشد. چند دقیقه بعد دوباره امتحان کن.");
            return;
        }
        if (!IsOk(res))
        {
            await Send(chatId, "نتیجه ترم الان در دسترس نیست. بعداً دوباره امتحان کن.");
            return;
        }
        if (!res!.Value.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object)
        {
            await Send(chatId, "هنوز نتیجه‌ای برای ترم فعلی تو محاسبه نشده.");
            return;
        }

        await Send(chatId, FormatStudentResult(r, TermFor(student.ClassCode)) + StaleNote(res));
    }

    static async Task StartClassResults(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /classresults را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        if (!Teachers.TryGetValue(userId, out var classes) || classes.Count == 0)
        {
            await Send(chatId, "این بخش فقط برای مدرس‌هاست.");
            return;
        }

        if (classes.Count == 1)
        {
            await ShowClassResults(chatId, classes[0].ClassCode);
            return;
        }

        var buttons = classes.Select(c =>
            new[] { InlineKeyboardButton.WithCallbackData(c.ClassCode, "cr|class|" + c.ClassCode) }).ToList();
        await bot.SendMessage(chatId, "نتایج کدام کلاس؟", replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task HandleClassResultsCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');

        if (parts.Length != 3 || parts[1] != "class" ||
            !Teachers.TryGetValue(userId, out var classes) || !classes.Any(c => c.ClassCode == parts[2]))
        {
            await bot.AnswerCallbackQuery(cq.Id);
            return;
        }

        await bot.AnswerCallbackQuery(cq.Id);
        await ShowClassResults(chatId, parts[2]);
    }

    static async Task ShowClassResults(long chatId, string classCode)
    {
        await Send(chatId, "در حال دریافت نتایج کلاس " + classCode + "...");

        await RecomputeForClass(classCode, "ClassResults.Recompute", force: true);

        var res = await PostToAppsScriptWithResult(new
        {
            action = "get_class_term_results",
            class_code = classCode,
            term_hint = TermFor(classCode)
        });

        if (!res.HasValue)
        {
            await Send(chatId, "ارتباط با سرور برقرار نشد. چند دقیقه بعد دوباره امتحان کن.");
            return;
        }
        if (!IsOk(res))
        {
            await Send(chatId, "❌ دریافت نتایج انجام نشد: " + JStr(res!.Value, "error"));
            return;
        }
        if (!res!.Value.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        {
            await Send(chatId, "برای کلاس " + classCode + " هنوز نتیجه‌ای محاسبه نشده (ترم فعال یا داده کافی وجود نداره).");
            return;
        }

        string termLabel = JStr(res!.Value, "term");
        if (termLabel == "") termLabel = TermFor(classCode);
        var sb = new StringBuilder();
        sb.Append("📊 نتایج کلاس " + classCode + "\n");
        sb.Append("ترم: " + termLabel + "\n\n");
        foreach (var it in arr.EnumerateArray())
        {
            string name = JStr(it, "full_name");
            if (name == "") name = JStr(it, "student_id");
            sb.Append(name + ": " + Fmt(it, "final_score", "") + " " + JStr(it, "status") + "\n");
        }
        sb.Append(StaleNote(res));
        sb.Append("\nℹ️ پیشنهاد سیستمی؛ نتیجه نهایی با تأیید آموزشگاه.");

        await SendLong(chatId, sb.ToString());
    }

    // فقط ادمین‌ها: /recompute_results یا /recompute_results CLASS-CODE
    static async Task StartRecomputeResults(Message message, string text)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        if (message.Chat.Type != ChatType.Private) return;
        if (!IsAcademyAdmin(userId)) return;

        string arg = text.Contains(' ') ? text.Substring(text.IndexOf(' ') + 1).Trim() : "";
        await Send(chatId, "در حال محاسبه نتایج ترم" + (arg == "" ? " (همه کلاس‌ها)" : " (کلاس " + arg + ")") + "...");

        var res = await PostToAppsScriptWithResult(new
        {
            action = "recompute_term_results",
            class_code = arg,
            term_hint = arg == "" ? ActiveTermId : TermFor(arg)
        });
        if (IsOk(res))
            await Send(chatId,
                "✅ نتایج ترم «" + JStr(res!.Value, "term") + "» محاسبه شد.\n" +
                "ردیف‌های محاسبه‌شده: " + JStr(res!.Value, "updated") + "\n" +
                "ردیف‌های قبلی که جایگزین شد: " + JStr(res!.Value, "replaced") +
                (JStr(res!.Value, "other_rows") != "0" && JStr(res!.Value, "other_rows") != ""
                    ? "\nردیف‌های ترم‌های دیگه (دست‌نخورده): " + JStr(res!.Value, "other_rows")
                    : ""));
        else
            await Send(chatId, "❌ محاسبه انجام نشد: " + (res.HasValue ? JStr(res.Value, "error") : "ارتباط با سرور برقرار نشد"));
    }

    // ===================== PLACEMENT (تعیین سطح) =====================

    static async Task<List<PlacementQuestion>> LoadPlacementBank()
    {
        if (PlacementBankCache != null && (DateTime.UtcNow - PlacementBankAt).TotalMinutes < 10)
            return PlacementBankCache;

        var list = new List<PlacementQuestion>();
        try
        {
            var res = await PostToAppsScriptWithResult(new { action = "get_placement_questions" });
            if (IsOk(res) && res!.Value.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (!int.TryParse(JStr(it, "book"), out int book)) continue;
                    if (!int.TryParse(JStr(it, "unit"), out int unit)) continue;

                    var opts = new List<string>();
                    if (it.TryGetProperty("options", out var oarr) && oarr.ValueKind == JsonValueKind.Array)
                        foreach (var o in oarr.EnumerateArray())
                            if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                                opts.Add(o.GetString()!);

                    string question = JStr(it, "question");
                    string correct = JStr(it, "correct");
                    if (question == "" || correct == "" || opts.Count < 2) continue;

                    list.Add(new PlacementQuestion
                    {
                        Id = JStr(it, "id"),
                        Book = book,
                        Unit = unit,
                        Type = JStr(it, "type"),
                        Question = question,
                        Options = opts,
                        Correct = correct
                    });
                }
            }
        }
        catch (Exception ex) { AppLog.Error("LoadPlacementBank", "exception", ex); }

        if (list.Count > 0)
        {
            PlacementBankCache = list;
            PlacementBankAt = DateTime.UtcNow;
            return list;
        }

        return PlacementBankCache ?? list;
    }

    // برای هر Unit (به‌ترتیب) یک سوال تصادفی از بانک انتخاب می‌کند و گزینه‌ها را بُر می‌زند
    static List<PlacementQuestion> BuildPlacementStage(List<PlacementQuestion> bank, int book)
    {
        var rnd = Random.Shared;
        var inBook = bank.Where(x => x.Book == book).ToList();
        var stage = new List<PlacementQuestion>();

        foreach (var unit in inBook.Select(x => x.Unit).Distinct().OrderBy(u => u))
        {
            var cands = inBook.Where(x => x.Unit == unit).ToList();
            var pick = cands[rnd.Next(cands.Count)];
            stage.Add(new PlacementQuestion
            {
                Id = pick.Id,
                Book = pick.Book,
                Unit = pick.Unit,
                Type = pick.Type,
                Question = pick.Question,
                Correct = pick.Correct,
                Options = pick.Options.OrderBy(_ => rnd.Next()).ToList()
            });
        }
        return stage;
    }

    static async Task StartPlacement(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /placement را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;

        // اگر اخیراً آزمون داده، نتیجه قبلی را نشان بده
        var status = await PostToAppsScriptWithResult(new
        {
            action = "placement_status",
            telegram_id = userId.ToString(),
            days = PlacementRetakeDays
        });

        if (IsOk(status) && JBool(status!.Value, "recent"))
        {
            string pb = JStr(status!.Value, "book");
            string pu = JStr(status!.Value, "unit");
            string pd = JStr(status!.Value, "date");
            bool all = JBool(status!.Value, "passed_all");

            await Send(chatId,
                "تو قبلاً (" + pd + ") تعیین سطح اولیه رو انجام دادی.\n\n" +
                (all
                    ? "نتیجه: همه بخش‌ها رو با موفقیت گذروندی؛ مدرس در جلسه آنلاین سطح دقیق رو مشخص می‌کنه."
                    : "نتیجه اولیه: کتاب Evolve " + pb + "، درس (Unit) " + pu + ".") +
                "\n\nآزمون رو هر " + PlacementRetakeDays + " روز فقط یک بار می‌شه داد. " +
                "زمان تعیین سطح آنلاین با مدرس رو همین‌جا بهت اطلاع می‌دیم.");
            return;
        }

        PlacementStates[userId] = new PlacementState
        {
            UserId = userId,
            Username = message.From.Username ?? "",
            TelegramName = ((message.From.FirstName ?? "") + " " + (message.From.LastName ?? "")).Trim(),
            Step = "intro"
        };

        var kb = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("▶️ شروع", "pl|start") }
        });

        await bot.SendMessage(chatId,
            "🎯 تعیین سطح آنلاین (مرحله اول)\n\n" +
            "• این آزمون بر اساس مجموعه کتاب‌های Evolve طراحی شده و حدود ۱۰ تا ۱۵ دقیقه طول می‌کشه.\n" +
            "• سوال‌ها از آسون شروع می‌شن و هر بخش مربوط به یکی از کتاب‌های Evolve (از ۱ تا ۶) است.\n" +
            "• اگر جواب سوالی رو نمی‌دونی، «نمی‌دانم» رو بزن. این کاملاً عادیه و کمک می‌کنه جای درست شروع رو پیدا کنیم.\n" +
            "• لطفاً تنهایی و بدون ترجمه، جستجو یا هوش مصنوعی جواب بده. اگر تقلب کنی، تو سطحی قرار می‌گیری که برات سخت‌تره و خودت ضرر می‌کنی.\n" +
            "• این تعیین سطح اولیه است. بعداً یک بار دیگه به‌صورت آنلاین با مدرس تعیین سطح می‌شی و سطح نهایی اون موقع مشخص می‌شه.\n" +
            "• ثبت‌نام اولیه رایگانه و فقط تو رو به آکادمی وصل می‌کنه. بعد از آزمون، زمان تعیین سطح آنلاین با مدرس رو " + InterviewWaitText + " همین‌جا بهت اطلاع می‌دیم.\n\n" +
            "برای شروع دکمه زیر رو بزن.",
            replyMarkup: kb);
    }

    static async Task AskPlacementName(long chatId, PlacementState state)
    {
        state.Step = "name";
        InlineKeyboardMarkup? kb = null;
        if (!string.IsNullOrWhiteSpace(state.TelegramName))
        {
            kb = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("استفاده از «" + state.TelegramName + "»", "pl|usename") }
            });
        }

        await bot.SendMessage(chatId,
            "اسم و فامیلت رو بنویس (تا آکادمی بتونه با تو هماهنگ کنه):",
            replyMarkup: kb);
    }

    static async Task AskPlacementPhone(long chatId, PlacementState state)
    {
        state.Step = "phone";
        var kb = new ReplyKeyboardMarkup(new[]
        {
            new[] { KeyboardButton.WithRequestContact("📱 ارسال شماره من") }
        })
        { ResizeKeyboard = true };

        await bot.SendMessage(chatId,
            "شماره تلفنت رو بفرست تا آکادمی بتونه برای تعیین سطح با تو در تماس باشه. (ارسال شماره الزامیه)\n\n" +
            "دکمه «ارسال شماره من» رو بزن، یا شماره رو تایپ کن.",
            replyMarkup: kb);
    }

    static async Task HandlePlacementText(Message message, PlacementState state, string text)
    {
        long chatId = message.Chat.Id;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Step == "name")
            {
                string name = text.Trim();
                if (name.Length < 2 || name.Length > 60)
                {
                    await Send(chatId, "اسم باید بین ۲ تا ۶۰ حرف باشه. دوباره بنویس.");
                    return;
                }
                state.FullName = name;
                await AskPlacementPhone(chatId, state);
                return;
            }

            if (state.Step == "prev")
            {
                await Send(chatId, "لطفاً با دکمه‌های «بله» یا «خیر» جواب بده.");
                return;
            }

            if (state.Step == "prevtext")
            {
                string bg = text.Trim();
                if (bg.Length < 3 || bg.Length > 500)
                {
                    await Send(chatId, "سابقه زبانت را بین ۳ تا ۵۰۰ حرف بنویس.");
                    return;
                }
                state.LanguageBackground = bg;
                await FinishPlacement(chatId, state);
                return;
            }

            if (state.Step == "phone")
            {
                int digits = text.Count(char.IsDigit);
                if (digits >= 8 && digits <= 15)
                {
                    state.Phone = text.Trim();
                    await BeginPlacementTest(chatId, state);
                    return;
                }

                await Send(chatId, "ارسال شماره الزامیه. دکمه «ارسال شماره من» رو بزن یا شماره رو (فقط عدد) تایپ کن.");
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task HandlePlacementContact(Message message, PlacementState state)
    {
        long chatId = message.Chat.Id;
        var contact = message.Contact!;

        if (contact.UserId != null && contact.UserId != message.From!.Id)
        {
            await Send(chatId, "لطفاً شماره خودت رو بفرست.");
            return;
        }

        await state.Gate.WaitAsync();
        try
        {
            if (state.Step != "phone") return;
            if (string.IsNullOrWhiteSpace(contact.PhoneNumber))
            {
                await Send(chatId, "شماره‌ای دریافت نشد. دوباره امتحان کن.");
                return;
            }
            state.Phone = contact.PhoneNumber;
            await BeginPlacementTest(chatId, state);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task AskPlacementPreviousStudy(long chatId, PlacementState state)
    {
        state.Step = "prev";

        // کیبورد «ارسال شماره» را با منوی اصلی عوض می‌کنیم
        await bot.SendMessage(chatId, "✅ آزمون تمام شد. چند سؤال کوتاه برای تکمیل پرونده:",
            replyMarkup: BuildMainMenu(Teachers.ContainsKey(state.UserId)));

        var kb = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ بله", "pl|prev|yes"),
                InlineKeyboardButton.WithCallbackData("❌ خیر", "pl|prev|no"),
            }
        });

        await bot.SendMessage(chatId, "آیا تا به حال سابقه زبان انگلیسی خواندن داشته‌ای؟", replyMarkup: kb);
    }

    static async Task BeginPlacementTest(long chatId, PlacementState state)
    {
        var bank = await LoadPlacementBank();
        if (bank.Count == 0)
        {
            PlacementStates.TryRemove(state.UserId, out _);
            AppLog.Error("Placement.Bank", "placement question bank is empty or failed to load", null, state.UserId);
            await bot.SendMessage(chatId,
                "❌ سوال‌های آزمون هنوز آماده نیست. کمی بعد دوباره /placement بزن.",
                replyMarkup: BuildMainMenu(Teachers.ContainsKey(state.UserId)));
            return;
        }

        state.Bank = bank;
        state.Step = "test";
        state.StartedUtc = DateTime.UtcNow;
        state.Book = 1;

        await bot.SendMessage(chatId,
            "✅ ثبت شد. آزمون شروع می‌شه. موفق باشی! 🍀",
            replyMarkup: BuildMainMenu(Teachers.ContainsKey(state.UserId)));

        await StartPlacementStage(chatId, state);
    }

    static async Task StartPlacementStage(long chatId, PlacementState state)
    {
        state.Current = BuildPlacementStage(state.Bank, state.Book);
        state.StageResults = new List<bool>();
        state.Index = 0;

        if (state.Current.Count == 0)
        {
            // برای این کتاب سوالی نیست: از همین کتاب شروع می‌کنیم
            state.ResultBook = state.Book;
            state.ResultUnit = 1;
            await AskPlacementPreviousStudy(chatId, state);
            return;
        }

        await SendPlacementQuestion(chatId, state);
    }

    static async Task SendPlacementQuestion(long chatId, PlacementState state)
    {
        var pq = state.Current[state.Index];
        state.Seq++;

        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < pq.Options.Count; i++)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(pq.Options[i], "pl|" + state.Seq + "|" + i) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🤷 نمی‌دانم", "pl|" + state.Seq + "|x") });

        await bot.SendMessage(chatId,
            "📘 بخش " + state.Book + " از " + PlacementBooks + "  ·  سوال " + (state.Index + 1) + " از " + state.Current.Count +
            "\n\n" + pq.Question,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandlePlacementCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 2) { await bot.AnswerCallbackQuery(cq.Id); return; }

        if (!PlacementStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /placement بزن.");
            return;
        }

        await state.Gate.WaitAsync();
        try
        {
            string action = parts[1];

            if (action == "start")
            {
                await bot.AnswerCallbackQuery(cq.Id);
                if (state.Step != "intro") return;
                await AskPlacementName(chatId, state);
                return;
            }

            if (action == "usename")
            {
                await bot.AnswerCallbackQuery(cq.Id);
                if (state.Step != "name") return;
                state.FullName = state.TelegramName;
                await AskPlacementPhone(chatId, state);
                return;
            }

            // پاسخ به سوال: pl|شماره_سوال|گزینه
            if (action == "prev" && parts.Length == 3)
            {
                await bot.AnswerCallbackQuery(cq.Id);
                if (state.Step != "prev") return;
                if (parts[2] != "yes" && parts[2] != "no") return;
                state.PreviousStudy = parts[2];
                if (parts[2] == "yes")
                {
                    state.Step = "prevtext";
                    await Send(chatId, "سابقه زبانت را بنویس (مثلاً: ۲ سال کلاس انگلیسی، سطح متوسط).");
                    return;
                }
                await FinishPlacement(chatId, state);
                return;
            }

            if (parts.Length == 3 && int.TryParse(parts[1], out int seq))
            {
                if (state.Step != "test" || seq != state.Seq || state.Index >= state.Current.Count)
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }

                var pq = state.Current[state.Index];
                bool correct = false;
                if (parts[2] != "x" && int.TryParse(parts[2], out int oi) && oi >= 0 && oi < pq.Options.Count)
                    correct = string.Equals(pq.Options[oi].Trim(), pq.Correct.Trim(), StringComparison.OrdinalIgnoreCase);

                state.StageResults.Add(correct);
                state.TotalAsked++;
                if (correct) state.TotalCorrect++;
                state.Index++;

                await bot.AnswerCallbackQuery(cq.Id, "✔ ثبت شد");

                if (state.Index < state.Current.Count)
                {
                    await SendPlacementQuestion(chatId, state);
                    return;
                }

                await EvaluatePlacementStage(chatId, state);
                return;
            }

            await bot.AnswerCallbackQuery(cq.Id);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // قبول شدن در یک بخش (کتاب) = حداقل ۸۰٪ درست. وگرنه همین کتاب نقطه شروعه
    static async Task EvaluatePlacementStage(long chatId, PlacementState state)
    {
        int correct = state.StageResults.Count(x => x);
        int total = state.Current.Count;
        int need = (int)Math.Ceiling(total * PlacementPassPercent / 100.0);
        state.BookScores[state.Book] = correct + "/" + total;

        if (correct >= need)
        {
            if (state.Book >= PlacementBooks)
            {
                state.PassedAll = true;
                state.ResultBook = PlacementBooks;
                state.ResultUnit = EvolveUnitsPerBook;
                await AskPlacementPreviousStudy(chatId, state);
                return;
            }

            state.Book++;
            await StartPlacementStage(chatId, state);
            return;
        }

        // رد شد: تعداد پاسخ‌های درستِ پشت سر هم از اول (سوال‌ها بر اساس Unit مرتب‌اند)
        int lead = 0;
        while (lead < state.StageResults.Count && state.StageResults[lead]) lead++;

        state.ResultBook = state.Book;
        state.ResultUnit = lead == 0 ? 1 : Math.Min(EvolveUnitsPerBook, state.Current[lead - 1].Unit + 1);
        await AskPlacementPreviousStudy(chatId, state);
    }

    static async Task FinishPlacement(long chatId, PlacementState state)
    {
        state.Step = "done";
        int seconds = (int)(DateTime.UtcNow - state.StartedUtc).TotalSeconds;
        string scores = string.Join("; ", state.BookScores.OrderBy(k => k.Key).Select(k => "B" + k.Key + ": " + k.Value));

        string result;
        if (state.PassedAll)
        {
            result =
                "✅ آزمون تموم شد. ممنون که وقت گذاشتی!\n\n" +
                "📚 مجموعه Evolve ۶ کتاب داره؛ از کتاب ۱ (شروع از پایه) تا کتاب ۶ (پیشرفته‌تر).\n\n" +
                "🎯 تو همه بخش‌ها (تا کتاب ۶) خیلی خوب جواب دادی. سطح تو بالاتر از چیزیه که این آزمون می‌سنجه و مدرس در جلسه آنلاین سطح دقیقت رو مشخص می‌کنه.\n\n";
        }
        else
        {
            result =
                "✅ آزمون تموم شد. ممنون که وقت گذاشتی!\n\n" +
                "📚 مجموعه Evolve ۶ کتاب داره؛ از کتاب ۱ (شروع از پایه) تا کتاب ۶ (پیشرفته‌تر).\n\n" +
                "🎯 نتیجه اولیه تو:\nکتاب " + state.ResultBook + " — درس (Unit) " + state.ResultUnit + "\n" +
                "یعنی پیشنهاد می‌کنیم آموزش رو از Unit " + state.ResultUnit + " کتاب Evolve " + state.ResultBook + " شروع کنی.\n\n";
        }

        result +=
            "⚠️ این تعیین سطح اولیه است و فقط یک پیشنهاده. بعداً یک بار دیگه به‌صورت آنلاین با مدرس تعیین سطح می‌شی و سطح نهایی اون موقع مشخص می‌شه.\n\n" +
            "📅 ثبت‌نام اولیه رایگانه و فقط تو رو به آکادمی وصل می‌کنه. زمان تعیین سطح آنلاین با مدرس رو " + InterviewWaitText + " همین‌جا بهت اطلاع می‌دیم.";

        await Send(chatId, result);

        // ثبت در شیت (تا ۲ بار تلاش)
        bool saved = false;
        string saveError = "";
        string placementAttemptId = "";
        for (int attempt = 1; attempt <= 2 && !saved; attempt++)
        {
            var (res, err) = await PostToAppsScriptDetailed(new
            {
                action = "log_placement_result",
                telegram_id = state.UserId.ToString(),
                username = state.Username,
                full_name = state.FullName,
                phone = state.Phone,
                book = state.ResultBook,
                unit = state.ResultUnit,
                passed_all = state.PassedAll,
                scores = scores,
                total_correct = state.TotalCorrect,
                total_asked = state.TotalAsked,
                duration_sec = seconds,
                previous_study = state.PreviousStudy,
                language_background = state.LanguageBackground
            });
            bool ok = IsOk(res);
            if (ok) placementAttemptId = JStr(res!.Value, "attempt_id");
            saved = ok;
            saveError = ok ? "" : (res.HasValue ? JStr(res.Value, "error") : err);
            if (!ok && string.IsNullOrWhiteSpace(saveError)) saveError = "پاسخ سرور ok نبود";
            if (!saved) await Task.Delay(1500);
        }

        if (!saved) AppLog.Error("Placement.Save", "log_placement_result failed: " + saveError, null, state.UserId);
        else AppLog.Info("Placement", "completed book=" + state.ResultBook + " unit=" + state.ResultUnit + " passed_all=" + state.PassedAll, state.UserId);
        await NotifyStaffPlacement(state, scores, seconds, saved ? "" : saveError);
        PlacementStates.TryRemove(state.UserId, out _);
        state.Scores = scores;
        await StartPlacementPreferences(chatId, state, placementAttemptId);
    }

    static async Task NotifyStaffPlacement(PlacementState state, string scores, int seconds, string saveError)
    {
        var sb = new StringBuilder();
        sb.Append("🆕 تعیین سطح اولیه جدید\n");
        sb.Append("نام: " + state.FullName + "\n");
        sb.Append("یوزرنیم: " + (string.IsNullOrWhiteSpace(state.Username) ? "—" : "@" + state.Username) + "\n");
        sb.Append("آیدی تلگرام: " + state.UserId + "\n");
        sb.Append("تلفن: " + (string.IsNullOrWhiteSpace(state.Phone) ? "—" : state.Phone) + "\n");
        sb.Append(state.PassedAll
            ? "نتیجه: همه ۶ بخش را گذراند (Evolve 6 یا بالاتر)\n"
            : "نتیجه: Evolve " + state.ResultBook + " / Unit " + state.ResultUnit + "\n");
        sb.Append("نمره بخش‌ها: " + scores + "\n");
        sb.Append("سابقه زبان قبلی: " + (state.PreviousStudy == "yes" ? "بله" : state.PreviousStudy == "no" ? "خیر" : "—") + "\n");
        if (!string.IsNullOrWhiteSpace(state.LanguageBackground))
            sb.Append("توضیح سابقه: " + state.LanguageBackground + "\n");
        sb.Append("مدت: " + (seconds / 60) + " دقیقه\n\n");
        sb.Append("برای اعلام زمان تعیین سطح با مدرس، ستون interview_time همین ردیف را در شیت Placement_Results پر کن؛ بات خودش به زبان‌آموز خبر می‌دهد.");
        if (!string.IsNullOrEmpty(saveError))
            sb.Append("\n\n⚠️ ثبت در شیت ناموفق بود: " + saveError);

        var targets = PlacementAdminIds.Distinct().ToList();
        foreach (var id in targets)
        {
            try { await bot.SendMessage(id, sb.ToString()); }
            catch (Exception ex) { AppLog.Warn("PlacementNotify", "staff " + id + " failed: " + ex.Message); }
        }
    }

    static async Task NotifyStaffPlacementPreferences(PlacementPreferenceState state)
    {
        var sb = new StringBuilder();
        sb.Append("🧩 ترجیحات تعیین سطح\n");
        sb.Append("نام: " + state.FullName + " | آیدی: " + state.UserId + "\n");
        sb.Append(state.PassedAll
            ? "نتیجه: همه بخش‌ها (Evolve 6+)\n"
            : "نتیجه: Evolve " + state.ResultBook + " / Unit " + state.ResultUnit + "\n");
        if (!string.IsNullOrWhiteSpace(state.Scores)) sb.Append("نمره بخش‌ها: " + state.Scores + "\n");
        sb.Append("سابقه زبان: " + (state.PreviousStudy == "yes" ? "بله" : state.PreviousStudy == "no" ? "خیر" : "—") + "\n");
        if (!string.IsNullOrWhiteSpace(state.LanguageBackground))
            sb.Append("توضیح سابقه: " + state.LanguageBackground + "\n");
        sb.Append("روزهای آزاد: " + (string.IsNullOrWhiteSpace(state.FreeDays) ? "—" : state.FreeDays) + "\n");
        sb.Append("بازه زمانی: " + (string.IsNullOrWhiteSpace(state.FreeTime) ? "—" : state.FreeTime) + "\n");
        sb.Append("شدت: " + state.Intensity + " (" + state.DaysPerWeek + " روز)\n");
        sb.Append("پکیج: " + state.CourseType);

        foreach (var id in PlacementAdminIds.Distinct())
        {
            try { await bot.SendMessage(id, sb.ToString()); }
            catch (Exception ex) { AppLog.Warn("PlacementNotify", "staff pref " + id + " failed: " + ex.Message); }
        }
    }

    // ===================== PHASE 3: PLACEMENT PREFERENCES =====================

    static readonly string[] PersianWeekdays = { "شنبه", "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه" };

    static InlineKeyboardMarkup PlacementDayButtons(string selected)
    {
        var picked = (selected ?? "").Split(new[] { '،', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var day in PersianWeekdays)
        {
            string label = (picked.Contains(day) ? "✅ " : "") + day;
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, "pp|day|" + day) });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ پایان انتخاب روزها", "pp|daysdone") });
        return new InlineKeyboardMarkup(rows);
    }

    static List<string> SplitTimeRanges(string? value)
    {
        return (value ?? "").Split(new[] { '،', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    // بازه‌های آزاد: چند بازه مجاز است؛ هم دکمه‌های آماده (چندتایی) و هم ورود دستی.
    static InlineKeyboardMarkup PlacementTimeButtons(string selected)
    {
        var picked = SplitTimeRanges(selected);
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < TermTimeSlots.Length; i += 2)
            rows.Add(TermTimeSlots.Skip(i).Take(2)
                .Select(t => InlineKeyboardButton.WithCallbackData((picked.Contains(t) ? "✅ " : "") + t, "pp|timeadd|" + t))
                .ToArray());
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✍️ ورود دستی بازه زمانی", "pp|timecustom") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ پایان انتخاب بازه‌ها", "pp|timedone") });
        return new InlineKeyboardMarkup(rows);
    }

    static async Task StartPlacementPreferences(long chatId, PlacementState placement, string attemptId = "")
    {
        long userId = placement.UserId;
        PlacementPreferenceStates[userId] = new PlacementPreferenceState
        {
            UserId = userId,
            TelegramId = userId.ToString(CultureInfo.InvariantCulture),
            AttemptId = attemptId,
            Step = "days",
            WaitingText = false,
            FullName = placement.FullName,
            Username = placement.Username,
            Phone = placement.Phone,
            ResultBook = placement.ResultBook,
            ResultUnit = placement.ResultUnit,
            PassedAll = placement.PassedAll,
            PreviousStudy = placement.PreviousStudy,
            LanguageBackground = placement.LanguageBackground,
            Scores = placement.Scores
        };
        await bot.SendMessage(chatId,
            "📅 روزهای آزادت را انتخاب کن (چند روز مجاز است):",
            replyMarkup: PlacementDayButtons(""));
    }

    static async Task HandlePlacementPreferenceText(Message message, PlacementPreferenceState state, string text)
    {
        long chatId = message.Chat.Id;

        // ورود دستی بازه(های) زمانی آزاد
        if (state.Step == "time" && state.WaitingText)
        {
            if (IsCommand(text, "/skip"))
            {
                state.WaitingText = false;
                await bot.SendMessage(chatId, "باشه. با دکمه‌ها بازه انتخاب کن یا «✍️ ورود دستی» را دوباره بزن.",
                    replyMarkup: PlacementTimeButtons(state.FreeTime));
                return;
            }

            var ranges = new List<string>();
            foreach (var piece in SplitTimeRanges(text))
            {
                if (!TryParseTimeRange(piece, out string rs, out string re)) continue;
                ranges.Add(rs + "-" + re);
            }
            if (ranges.Count == 0)
            {
                await Send(chatId,
                    "قالب زمان نامعتبر است. مثلاً: 16:30-18:00\nچند بازه را با کاما جدا کن: 10:00-12:00، 16:30-18:00\nبرای لغو: /cancel");
                return;
            }
            state.FreeTime = string.Join("، ", ranges);
            state.WaitingText = false;
            await bot.SendMessage(chatId,
                "✅ ثبت شد: " + state.FreeTime + "\nاگر بازه دیگری هم داری اضافه کن، وگرنه «✅ پایان انتخاب بازه‌ها» را بزن.",
                replyMarkup: PlacementTimeButtons(state.FreeTime));
            return;
        }

        await Send(chatId, "لطفاً از دکمه‌های همین پیام استفاده کن.");
    }

    static async Task HandlePlacementPreferenceCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 2) { await bot.AnswerCallbackQuery(cq.Id); return; }

        if (!PlacementPreferenceStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /placement بزن.");
            return;
        }

        if (parts[1] == "day" && parts.Length >= 3)
        {
            var picked = state.FreeDays.Split(new[] { '،', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (picked.Contains(parts[2])) picked.Remove(parts[2]); else picked.Add(parts[2]);
            state.FreeDays = string.Join("، ", PersianWeekdays.Where(d => picked.Contains(d)));
            await bot.AnswerCallbackQuery(cq.Id);
            await bot.SendMessage(chatId, "📅 روزهای آزادت را انتخاب کن (چند روز مجاز است):",
                replyMarkup: PlacementDayButtons(state.FreeDays));
            return;
        }

        if (parts[1] == "daysdone")
        {
            if (string.IsNullOrWhiteSpace(state.FreeDays))
            {
                await bot.AnswerCallbackQuery(cq.Id, "حداقل یک روز را انتخاب کن.");
                return;
            }
            state.Step = "time";
            await bot.AnswerCallbackQuery(cq.Id);
            await bot.SendMessage(chatId,
                "⏰ بازه(های) زمانی آزادت را انتخاب کن (می‌توانی چند بازه انتخاب کنی، یا «✍️ ورود دستی» بزنی):",
                replyMarkup: PlacementTimeButtons(state.FreeTime));
            return;
        }

        // افزودن/حذف یک بازه آماده (چند بازه مجاز)
        if (parts[1] == "timeadd" && parts.Length >= 3)
        {
            var picked = SplitTimeRanges(state.FreeTime);
            if (picked.Contains(parts[2])) picked.Remove(parts[2]); else picked.Add(parts[2]);
            state.FreeTime = string.Join("، ", picked);
            await bot.AnswerCallbackQuery(cq.Id);
            await bot.SendMessage(chatId,
                "⏰ بازه(های) زمانی آزادت را انتخاب کن (می‌توانی چند بازه انتخاب کنی، یا «✍️ ورود دستی» بزنی):",
                replyMarkup: PlacementTimeButtons(state.FreeTime));
            return;
        }

        // ورود دستی بازه(های) زمانی
        if (parts[1] == "timecustom")
        {
            state.WaitingText = true;
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId,
                "⏰ بازه(های) زمانی آزادت را بنویس (مثلاً 16:30-18:00؛ چند بازه با کاما: 10:00-12:00، 16:30-18:00).\nبرای لغو: /cancel");
            return;
        }

        // پایان انتخاب بازه‌ها → برو به مرحله شدت
        if (parts[1] == "timedone")
        {
            if (string.IsNullOrWhiteSpace(state.FreeTime))
            {
                await bot.AnswerCallbackQuery(cq.Id, "حداقل یک بازه زمانی انتخاب یا وارد کن.");
                return;
            }
            state.Step = "intensity";
            await bot.AnswerCallbackQuery(cq.Id);
            var rows = new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("Normal (۲ روز)", "pp|int|Normal|2") },
                new[] { InlineKeyboardButton.WithCallbackData("Semi-intensive (۳ روز)", "pp|int|Semi-intensive|3") },
                new[] { InlineKeyboardButton.WithCallbackData("Intensive (۴ روز)", "pp|int|Intensive|4") }
            };
            await bot.SendMessage(chatId, "📦 شدت دوره را انتخاب کن:", replyMarkup: new InlineKeyboardMarkup(rows));
            return;
        }

        if (parts[1] == "int" && parts.Length >= 4)
        {
            state.Intensity = parts[2];
            state.DaysPerWeek = int.TryParse(parts[3], out int days) ? days : 0;
            state.Step = "course";
            await bot.AnswerCallbackQuery(cq.Id);
            var rows = new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("کتاب‌محور — بر اساس کتاب Evolve", "pp|course|کتاب‌محور") },
                new[] { InlineKeyboardButton.WithCallbackData("Story Mode — درس‌های Evolve به صورت داستانی", "pp|course|Story Mode") },
                new[] { InlineKeyboardButton.WithCallbackData("Talk On — مکالمه‌محور محض", "pp|course|Talk On") },
                new[] { InlineKeyboardButton.WithCallbackData("Discussion — سطوح بالا، بحث هدفمند و تفکر نقادانه", "pp|course|Discussion") }
            };
            await bot.SendMessage(chatId, "📚 نوع دوره را انتخاب کن:", replyMarkup: new InlineKeyboardMarkup(rows));
            return;
        }

        if (parts[1] == "course" && parts.Length >= 3)
        {
            state.CourseType = parts[2];
            await bot.AnswerCallbackQuery(cq.Id);
            var (ok, err) = await PostToAppsScriptWithError(new
            {
                action = "save_placement_preferences",
                telegram_id = state.TelegramId,
                attempt_id = state.AttemptId,
                free_days = state.FreeDays,
                free_time = state.FreeTime,
                intensity = state.Intensity,
                days_per_week = state.DaysPerWeek,
                course_type = state.CourseType
            });
            PlacementPreferenceStates.TryRemove(userId, out _);
            if (ok)
            {
                await Send(chatId, "✅ ترجیحات تعیین سطح ذخیره شد.");
                await NotifyStaffPlacementPreferences(state);
            }
            else
                await Send(chatId, "⚠️ ذخیره ترجیحات انجام نشد: " + err);
            return;
        }

        await bot.AnswerCallbackQuery(cq.Id);
    }

    // ===================== PHASE 3: DAILY GRAMMAR =====================

    static async Task StartDailyGrammar(Message message)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /dailygrammar را در چت خصوصی بزن.");
            return;
        }

        // برای staff، شروع آزمون گرامر برای خودشان بی‌معنی است؛ مستقیم لیست بررسی کلاس را نشان بده.
        if (IsStaffUser(userId))
        {
            await StartPendingGrammar(message);
            return;
        }

        if (PracticeStates.TryGetValue(userId, out var activePractice) &&
            activePractice.Index < activePractice.Questions.Count)
        {
            await Send(chatId, "اول جلسه تمرین فعلی را تمام کن، بعد سوال روز را بگیر.");
            return;
        }

        var student = await FindStudentFull(userId.ToString(CultureInfo.InvariantCulture));
        string studentId = student?.StudentId ?? userId.ToString(CultureInfo.InvariantCulture);
        string classCode = student?.ClassCode ?? "";
        string level = !string.IsNullOrWhiteSpace(student?.Level) ? student!.Level : GetClassLevel(classCode);
        string unitLesson = NormalizeUnitLesson(student?.CurrentUnit, student?.CurrentLesson, "1_1");

        await SendDailyGrammarToStudent(chatId, userId, studentId, classCode, level, unitLesson, true);
    }

    static async Task SendDailyGrammarToStudent(long chatId, long userId, string studentId,
        string classCode, string level, string unitLesson, bool notifyGroup, bool quiet = false)
    {
        var stateCheck = await PostToAppsScriptWithResult(new
        {
            action = "daily_grammar_state",
            student_id = studentId,
            date = TodayStr()
        });
        if (IsOk(stateCheck) && JBool(stateCheck!.Value, "found"))
        {
            AppLog.Info("DailyGrammar", "skipped: already has today's prompt", userId);
            if (!quiet) await Send(chatId, "امروز یک سوال گرامر برایت ثبت شده است. فردا دوباره امتحان کن.");
            return;
        }

        var all = await LoadAllPracticeQuestions();
        var eligible = all.Where(q => IsDailyPromptType(q.QuestionType)).ToList();
        if (eligible.Count == 0)
        {
            AppLog.Info("DailyGrammar", "no eligible questions", userId);
            if (!quiet) await Send(chatId, "امروز سوال گرامری مناسب پیدا نشد. بعداً دوباره امتحان کن.");
            return;
        }

        var storyMap = new Dictionary<string, StoryInfo?>(StringComparer.OrdinalIgnoreCase);
        var levelCandidates = new List<PracticeQuestion>();
        var unitCandidates = new List<PracticeQuestion>();
        foreach (var q in eligible)
        {
            if (!storyMap.TryGetValue(q.StoryId, out var story))
            {
                story = await LoadStory(q.StoryId);
                storyMap[q.StoryId] = story;
            }
            if (story == null) continue;
            string storyUnit = (story.Unit ?? "").Split('_')[0].Trim();
            // سطح باید بخورد؛ سوال از سطح دیگر فرستاده نمی‌شود
            if (!LevelMatchesStory(level, storyUnit))
                continue;
            levelCandidates.Add(q);
            string storyUL = NormalizeUnitLesson(story.Unit, story.Lesson, storyUnit);
            if (StoryMatchesLesson(storyUL, unitLesson))
                unitCandidates.Add(q);
        }
        // اول درس جاری؛ اگر سوالی نبود، فقط در همان سطح+یونیت (هر درس) عقب‌نشینی می‌کنیم.
        // هرگز به سوالات سطح/یونیت دیگر نمی‌رویم.
        var sameUnitCandidates = levelCandidates
            .Where(q =>
            {
                var st = storyMap[q.StoryId];
                string ul = st == null ? "" : NormalizeUnitLesson(st.Unit, st.Lesson, st.Unit ?? "");
                return StoryMatchesUnit(ul, unitLesson);
            })
            .ToList();
        var candidates = unitCandidates.Count > 0 ? unitCandidates : sameUnitCandidates;
        if (candidates.Count == 0)
        {
            AppLog.Info("DailyGrammar", "no candidates for level=" + level + " unit=" + unitLesson, userId);
            if (!quiet) await Send(chatId, "امروز سوال گرامری مناسب پیدا نشد. بعداً دوباره امتحان کن.");
            return;
        }

        // تکرار نزدیک را رد کن؛ اگر گزینه‌ای نماند همان لیست را استفاده کن
        var notRecent = candidates
            .Where(q => (DateTime.UtcNow - QuestionLastAskedAt(studentId, q.QuestionId)).TotalDays >= 3)
            .ToList();
        if (notRecent.Count > 0) candidates = notRecent;

        var ordered = OrderByCoverage(studentId, candidates);
        var oldestAsked = QuestionLastAskedAt(studentId, ordered[0].QuestionId);
        var tier = ordered.Where(q => QuestionLastAskedAt(studentId, q.QuestionId) == oldestAsked).ToList();
        var dailyQuestion = tier[Random.Shared.Next(tier.Count)];
        QuestionLastAsked[studentId + "|" + dailyQuestion.QuestionId] = DateTime.UtcNow;
        ParseUL(unitLesson, out int u, out int l);
        string studentName = (await FindStudentFull(studentId))?.FullName ?? "";

        DailyGrammarStates[userId] = new DailyGrammarState
        {
            UserId = userId,
            StudentId = studentId,
            StudentName = studentName,
            ClassCode = classCode,
            Level = level,
            Unit = u.ToString(CultureInfo.InvariantCulture),
            Lesson = l.ToString(CultureInfo.InvariantCulture),
            QuestionId = dailyQuestion.QuestionId,
            Question = dailyQuestion.Question,
            Options = dailyQuestion.Options.ToList(),
            CorrectAnswer = ResolveCorrect(dailyQuestion),
            WaitingAnswer = true
        };

        var (ok, err) = await PostToAppsScriptWithError(new
        {
            action = "log_daily_grammar",
            student_id = studentId,
            student_name = studentName,
            class_code = classCode,
            level = level,
            unit = u,
            lesson = l,
            question_id = dailyQuestion.QuestionId
        });
        if (!ok)
        {
            DailyGrammarStates.TryRemove(userId, out _);
            if (!quiet) await Send(chatId, "❌ ذخیره سوال روزانه انجام نشد: " + err);
            return;
        }

        var dailyState = DailyGrammarStates[userId];
        var answerRows = new List<InlineKeyboardButton[]>();
        if (dailyState.Options.Count > 0 && !string.IsNullOrWhiteSpace(dailyState.CorrectAnswer))
        {
            for (int i = 0; i < dailyState.Options.Count; i++)
                answerRows.Add(new[] { InlineKeyboardButton.WithCallbackData(dailyState.Options[i], "dg|a|" + i) });
        }

        if (answerRows.Count > 0)
        {
            await bot.SendMessage(chatId,
                "📘 گرامر روز\n\n" + dailyQuestion.Question + "\n\nیکی از گزینه‌ها را انتخاب کن.",
                replyMarkup: new InlineKeyboardMarkup(answerRows));
        }
        else
        {
            await Send(chatId, "📘 گرامر روز\n\n" + dailyQuestion.Question + "\n\nجوابت را تایپ کن یا /skip بزن.");
        }

        if (notifyGroup && classCode.Length > 0)
        {
            long groupId = GetClassGroupId(classCode);
            if (groupId != 0)
            {
                try { await bot.SendMessage(groupId, "📘 گرامر روز برای زبان‌آموز " + studentId + ":\n\n" + dailyQuestion.Question); }
                catch (Exception ex) { AppLog.Warn("DailyGrammar.Group", ex.Message, userId); }
            }
        }
    }

    static bool LevelMatchesStory(string? level, string? storyUnit)
    {
        level = (level ?? "").Trim();
        storyUnit = (storyUnit ?? "").Trim();
        if (level.Length == 0 || storyUnit.Length == 0) return true;
        if (!int.TryParse(level, out _)) return true; // سطح غیرعددی: فیلتر نکن

        string storyUnitNum = storyUnit.Split('_')[0].Trim();
        if (storyUnitNum.Length == 0) return true;
        if (string.Equals(storyUnitNum, level, StringComparison.OrdinalIgnoreCase)) return true;

        // 101 -> کتاب ۱، یونیت ۱. ستون unit داستان ممکن است یونیت یا شماره کتاب باشد؛
        // هر دو حالت را می‌پذیریم تا سوال سطح دیگر رد شود ولی کتاب‌های بعدی هم قفل نشوند.
        if (level.Length >= 3)
        {
            string bookPart = level.Substring(0, 1);
            string unitPart = level.Substring(1);
            if (string.Equals(storyUnitNum, bookPart, StringComparison.OrdinalIgnoreCase)) return true;
            if (int.TryParse(unitPart, out int wantedUnit) &&
                int.TryParse(storyUnitNum, out int storyUnitNumber) && storyUnitNumber == wantedUnit)
                return true;
        }
        return false;
    }

    // درس جاری: "1_3" دقیقاً برابر "1_3"
    static bool StoryMatchesLesson(string? storyUnitLesson, string? unitLesson)
    {
        string a = (storyUnitLesson ?? "").Trim();
        string b = (unitLesson ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // همان یونیت (هر درس): "1_3" با "1_5" هم‌یونیت است
    static bool StoryMatchesUnit(string? storyUnitLesson, string? unitLesson)
    {
        string a = (storyUnitLesson ?? "").Trim();
        string b = (unitLesson ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        string aUnit = a.Split('_')[0].Trim();
        string bUnit = b.Split('_')[0].Trim();
        return aUnit.Length > 0 && string.Equals(aUnit, bUnit, StringComparison.OrdinalIgnoreCase);
    }

    static async Task HandleDailyGrammarAnswer(Message message, DailyGrammarState state, string text)
    {
        long chatId = message.Chat.Id;
        bool skip = IsCommand(text, "/skip");
        string answer = skip ? "" : text.Trim();

        var (ok, err) = await PostToAppsScriptWithError(new
        {
            action = "log_daily_grammar_answer",
            student_id = state.StudentId,
            student_name = state.StudentName,
            class_code = state.ClassCode,
            question_id = state.QuestionId,
            date = TodayStr(),
            answer = answer,
            status = "submitted"
        });

        state.WaitingAnswer = false;
        state.AwaitingSelfMark = false;
        if (!ok)
        {
            await Send(chatId, "❌ ثبت جواب انجام نشد: " + err);
            DailyGrammarStates.TryRemove(state.UserId, out _);
            return;
        }

        DailyGrammarStates.TryRemove(state.UserId, out _);
        if (skip)
        {
            await Send(chatId, "رد شد. جواب مدل:\n" +
                (string.IsNullOrWhiteSpace(state.CorrectAnswer) ? "—" : state.CorrectAnswer));
            return;
        }

        if (state.Options.Count > 0 && !string.IsNullOrWhiteSpace(state.CorrectAnswer))
        {
            bool correct = NormalizeAnswer(answer) == NormalizeAnswer(state.CorrectAnswer);
            await Send(chatId, correct
                ? "✅ ثبت شد. درست بود."
                : "❌ ثبت شد.\nجواب درست: " + state.CorrectAnswer);
            return;
        }

        await Send(chatId, "✅ ثبت شد.");
    }

    static async Task HandleDailyGrammarCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3 || parts[1] != "a") { await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمی است."); return; }
        if (!DailyGrammarStates.TryGetValue(userId, out var state) || !state.WaitingAnswer)
        {
            await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمی است.");
            return;
        }

        if (!int.TryParse(parts[2], out int optionIndex) ||
            optionIndex < 0 || optionIndex >= state.Options.Count)
        {
            await bot.AnswerCallbackQuery(cq.Id, "این گزینه معتبر نیست.");
            return;
        }

        string answer = state.Options[optionIndex].Trim();
        bool correct = string.Equals(answer, state.CorrectAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
        state.WaitingAnswer = false;
        state.AwaitingSelfMark = false;
        var (ok, err) = await PostToAppsScriptWithError(new
        {
            action = "log_daily_grammar_answer",
            student_id = state.StudentId,
            student_name = state.StudentName,
            class_code = state.ClassCode,
            question_id = state.QuestionId,
            date = TodayStr(),
            answer = answer,
            status = "submitted"
        });

        DailyGrammarStates.TryRemove(userId, out _);
        if (!ok)
        {
            await bot.AnswerCallbackQuery(cq.Id, "ثبت نشد");
            await Send(chatId, "❌ ثبت جواب انجام نشد: " + err);
            return;
        }

        await bot.AnswerCallbackQuery(cq.Id, correct ? "✅" : "❌");
        if (correct)
            PostInBackground(new { action = "mark_correct", student_id = state.StudentId, question_id = state.QuestionId });
        else
            PostInBackground(new
            {
                action = "log_mistake",
                student_id = state.StudentId,
                student_name = GroupStudentName(state.StudentId),
                question_id = state.QuestionId,
                skill = "DailyPrompt"
            });

        await Send(chatId, correct
            ? "✅ ثبت شد. درست بود."
            : "❌ ثبت شد.\nجواب درست: " + state.CorrectAnswer);
    }

    // بررسی پاسخ DailyPrompt توسط مدرس: 👍 = قبول (بدون اشتباه)، 👎 = رد (افزودن به Student_Mistakes)
    static async Task HandleDailyGrammarReviewCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        int messageId = cq.Message.MessageId;

        var parts = cq.Data.Split('|');
        if (parts.Length < 3 || !int.TryParse(parts[2], out int row) || row <= 0)
        {
            await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمی است.");
            return;
        }

        bool like = parts[1] == "ok";
        string emoji = like ? "👍" : "👎";

        var res = await PostToAppsScriptWithResult(new
        {
            action = "review_daily_grammar",
            row = row,
            emoji = emoji,
            teacher_id = userId.ToString(CultureInfo.InvariantCulture)
        });

        if (!IsOk(res))
        {
            await bot.AnswerCallbackQuery(cq.Id, "ثبت نشد");
            return;
        }

        // آیتم از لیست در انتظار خارج شد؛ دکمه‌ها هم برداشته می‌شوند تا دوباره بررسی نشود
        await bot.AnswerCallbackQuery(cq.Id, emoji);
        try { await bot.EditMessageReplyMarkup(chatId, messageId, (InlineKeyboardMarkup?)null); }
        catch (Exception ex) { AppLog.Warn("DailyGrammar.Review", "remove buttons failed: " + ex.Message); }
    }

    // وضعیت ارسال/پاسخ DailyPrompt امروز برای کلاس‌های مدرس یا همه کلاس‌ها (ادمین)
    static async Task StartDailyPromptStatus(Message message)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /dailypromptstatus را در چت خصوصی بزن.");
            return;
        }
        if (!IsStaffUser(userId))
        {
            await Send(chatId, "شما دسترسی ندارید.");
            return;
        }

        bool isAdmin = IsAcademyAdmin(userId);
        var classes = GetScheduleClasses(userId, isAdmin);
        if (classes.Count == 0)
        {
            await Send(chatId, "کلاسی برای شما ثبت نشده است.");
            return;
        }

        string today = TodayStr();
        var sb = new StringBuilder("📊 وضعیت DailyPrompt امروز (" + today + ")\n");
        foreach (var c in classes)
        {
            var res = await PostToAppsScriptWithResult(new
            {
                action = "daily_prompt_status",
                class_code = c.ClassCode,
                date = today
            });
            sb.Append("\n📚 " + c.ClassCode + "\n");
            if (!IsOk(res) || !res!.Value.TryGetProperty("items", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                sb.Append("— وضعیت خوانده نشد\n");
                continue;
            }

            int total = 0, sent = 0, answered = 0;
            var lines = new List<string>();
            foreach (var it in arr.EnumerateArray())
            {
                total++;
                bool wasSent = Eq(JStr(it, "sent"), "yes");
                bool wasAnswered = Eq(JStr(it, "answered"), "yes");
                if (wasSent) sent++;
                if (wasAnswered) answered++;
                string name = JStr(it, "student_name");
                if (string.IsNullOrWhiteSpace(name)) name = JStr(it, "student_id");
                string mark = !wasSent ? "➖ نفرستاده" : wasAnswered ? "✅ جواب داده" : "⏳ بی‌جواب";
                lines.Add("• " + name + " — " + mark);
            }
            sb.Append("دریافت: " + sent + " | جواب: " + answered + " | کل: " + total + "\n");
            foreach (var line in lines) sb.Append(line + "\n");
        }
        await SendLong(chatId, sb.ToString());
    }

    static async Task StartPendingGrammar(Message message)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        bool isAdmin = IsAcademyAdmin(userId);
        bool isTeacher = Teachers.TryGetValue(userId, out var classes) && classes.Count > 0;
        if (!isAdmin && !isTeacher)
        {
            await Send(chatId, "شما دسترسی ندارید.");
            return;
        }

        var questionMap = (await LoadAllPracticeQuestions())
            .Where(q => !string.IsNullOrWhiteSpace(q.QuestionId))
            .GroupBy(q => q.QuestionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Question, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        if (isAdmin)
        {
            var res = await PostToAppsScriptWithResult(new { action = "list_daily_grammar", status = "pending" });
            count += await SendPendingGrammarItems(chatId, res, questionMap);
        }
        else
        {
            foreach (var c in classes!)
            {
                var res = await PostToAppsScriptWithResult(new
                {
                    action = "list_daily_grammar",
                    class_code = c.ClassCode,
                    status = "pending"
                });
                count += await SendPendingGrammarItems(chatId, res, questionMap);
            }
        }
        if (count == 0)
        {
            await Send(chatId, "موردی در انتظار بررسی نیست.");
            return;
        }
        await Send(chatId, "📘 " + count + " پاسخ در انتظار بررسی است. برای هر مورد 👍 یا 👎 بزن.");
    }

    // هر پاسخ DailyPrompt به‌صورت یک آیتم جدا برای مدرس فرستاده می‌شود (نه یک لیست بزرگ)
    static async Task<int> SendPendingGrammarItems(long chatId, JsonElement? res,
        Dictionary<string, string> questionMap)
    {
        if (!IsOk(res) || !res!.Value.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return 0;
        int n = 0;
        foreach (var it in arr.EnumerateArray())
        {
            int row = (int)(JNum(it, "row") ?? 0);
            if (row <= 0) continue;

            string studentId = JStr(it, "student_id");
            string studentName = JStr(it, "student_name");
            if (string.IsNullOrWhiteSpace(studentName))
            {
                var student = await FindStudentFull(studentId);
                studentName = student?.FullName ?? "";
            }
            if (string.IsNullOrWhiteSpace(studentName)) studentName = studentId;

            string questionId = JStr(it, "question_id");
            string questionText = questionMap.TryGetValue(questionId, out var q) ? q : "";
            string answer = JStr(it, "answer");

            string text =
                "👤 زبان‌آموز: " + studentName + "\n" +
                "🏫 کلاس: " + JStr(it, "class_code") + "\n" +
                "❓ سوال:\n" + (string.IsNullOrWhiteSpace(questionText) ? "—" : questionText) + "\n" +
                "✍️ جواب:\n" + (string.IsNullOrWhiteSpace(answer) ? "—" : answer);

            var markup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("👍", "dgr|ok|" + row),
                    InlineKeyboardButton.WithCallbackData("👎", "dgr|no|" + row)
                }
            });

            try
            {
                await bot.SendMessage(chatId, text, replyMarkup: markup);
                n++;
            }
            catch (Exception ex)
            {
                AppLog.Warn("DailyGrammar.Review", "send item failed: " + ex.Message);
            }
        }
        return n;
    }

    // ===================== PHASE 3: CLASS SCHEDULE / AVAILABILITY =====================

    static async Task StartClassSchedule(Message message)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /classschedule را در چت خصوصی بزن.");
            return;
        }

        bool isAdmin = IsAcademyAdmin(userId);
        var classes = GetScheduleClasses(userId, isAdmin);
        if (classes.Count == 0)
        {
            await Send(chatId, "کلاسی برای شما ثبت نشده است.");
            return;
        }

        var sb = new StringBuilder("🗓️ برنامه هفتگی کلاس‌ها (شیت Class_Schedule)\n");
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var c in classes)
        {
            var res = await PostToAppsScriptWithResult(new
            {
                action = "list_class_schedule",
                class_code = c.ClassCode
            });
            sb.Append("\n📚 " + c.ClassCode + "\n");
            int count = 0;
            if (IsOk(res) && res!.Value.TryGetProperty("items", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    string day = JStr(it, "weekday");
                    if (string.IsNullOrWhiteSpace(day)) continue;
                    string start = JStr(it, "start_time");
                    string end = JStr(it, "end_time");
                    sb.Append("• " + day + ": " + (start.Length == 0 ? "—" : start) +
                        " - " + (end.Length == 0 ? "—" : end) + "\n");
                    count++;
                }
            }
            if (count == 0) sb.Append("— برنامه‌ای ثبت نشده\n");
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✏️ ویرایش " + c.ClassCode, "cs|edit|" + c.ClassCode) });
        }

        await SendLong(chatId, sb.ToString());
        if (rows.Count > 0)
            await bot.SendMessage(chatId, "برای ثبت/ویرایش برنامه، کلاس را انتخاب کن:",
                replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static List<TeacherClass> GetScheduleClasses(long userId, bool isAdmin)
    {
        var result = new List<TeacherClass>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isAdmin)
        {
            foreach (var kv in ClassRegistry)
                if (seen.Add(kv.Key))
                    result.Add(new TeacherClass
                    {
                        ClassCode = kv.Key,
                        TeacherName = kv.Value.TeacherName,
                        Level = kv.Value.Level
                    });
            foreach (var kv in Teachers)
                foreach (var tc in kv.Value)
                    if (seen.Add(tc.ClassCode)) result.Add(tc);
        }
        else if (Teachers.TryGetValue(userId, out var classes))
        {
            foreach (var tc in classes)
                if (seen.Add(tc.ClassCode)) result.Add(tc);
        }
        return result.OrderBy(c => c.ClassCode, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task AskScheduleWeekday(long chatId, string prompt, string prefix)
    {
        var rows = PersianWeekdays.Select(d => new[] { InlineKeyboardButton.WithCallbackData(d, prefix + d) }).ToList();
        await bot.SendMessage(chatId, prompt, replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task ShowClassScheduleForClass(long chatId, string classCode)
    {
        var res = await PostToAppsScriptWithResult(new
        {
            action = "list_class_schedule",
            class_code = classCode
        });
        var sb = new StringBuilder("🗓️ برنامه هفتگی کلاس " + classCode + "\n");
        int count = 0;
        if (IsOk(res) && res!.Value.TryGetProperty("items", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                string day = JStr(it, "weekday");
                if (string.IsNullOrWhiteSpace(day)) continue;
                string start = JStr(it, "start_time");
                string end = JStr(it, "end_time");
                sb.Append("• " + day + ": " + (start.Length == 0 ? "—" : start) +
                    " - " + (end.Length == 0 ? "—" : end) + "\n");
                count++;
            }
        }
        if (count == 0) sb.Append("— برنامه‌ای ثبت نشده\n");
        await SendLong(chatId, sb.ToString());
    }

    static async Task HandleClassScheduleCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3) { await bot.AnswerCallbackQuery(cq.Id); return; }

        if (parts[1] == "edit")
        {
            string cc = parts[2];
            if (!CanManageClass(userId, cc))
            {
                await bot.AnswerCallbackQuery(cq.Id, "این کلاس برای شما ثبت نشده.");
                return;
            }
            long teacherId = userId;
            string teacherName = "";
            var tc = Teachers.TryGetValue(userId, out var ownClasses)
                ? ownClasses.FirstOrDefault(c => Eq(c.ClassCode, cc))
                : null;
            if (tc != null) teacherName = tc.TeacherName;
            else if (ClassRegistry.TryGetValue(cc, out var reg))
            {
                teacherName = reg.TeacherName;
                if (reg.TeacherId != 0) teacherId = reg.TeacherId;
            }
            TeacherScheduleStates[userId] = new TeacherScheduleState
            {
                TeacherId = teacherId,
                TeacherName = teacherName,
                ClassCode = cc,
                Step = "weekday"
            };
            await bot.AnswerCallbackQuery(cq.Id);
            await AskScheduleWeekday(chatId,
                "🗓️ برنامه هفتگی کلاس (شیت Class_Schedule)\nکلاس " + cc + " — روز را انتخاب کن:",
                "cs|day|");
            return;
        }

        if (!TeacherScheduleStates.TryGetValue(userId, out var state) || state.Availability)
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده.");
            return;
        }
        if (parts[1] == "class")
        {
            if (!Teachers.TryGetValue(userId, out var classes)) return;
            var tc = classes.FirstOrDefault(c => c.ClassCode == parts[2]);
            if (tc == null) { await bot.AnswerCallbackQuery(cq.Id); return; }
            state.ClassCode = tc.ClassCode;
            state.TeacherName = tc.TeacherName;
            await bot.AnswerCallbackQuery(cq.Id);
            await AskScheduleWeekday(chatId, "🗓️ برنامه هفتگی کلاس (شیت Class_Schedule)\nکلاس " + tc.ClassCode + " — روز را انتخاب کن:", "cs|day|");
            return;
        }
        if (parts[1] == "set")
        {
            state.Step = "time";
            state.WaitingText = true;
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "⏰ بازه زمانی " + state.Weekday + " را بنویس (مثلاً: ۱۸:۰۰ تا ۲۰:۰۰).\nبرای لغو: /cancel");
            return;
        }

        if (parts[1] == "del")
        {
            string cc = state.ClassCode;
            string day = state.Weekday;
            var (ok, err) = await PostToAppsScriptWithError(new
            {
                action = "delete_class_schedule",
                class_code = cc,
                weekday = day
            });
            TeacherScheduleStates.TryRemove(userId, out _);
            await bot.AnswerCallbackQuery(cq.Id);
            if (!ok)
            {
                await Send(chatId, "❌ حذف نشد: " + err);
                return;
            }
            await Send(chatId, "🗑️ روز " + day + " از برنامه " + cc + " حذف شد.");
            await ShowClassScheduleForClass(chatId, cc);
            return;
        }

        if (parts[1] != "day") { await bot.AnswerCallbackQuery(cq.Id); return; }
        state.Weekday = parts[2];
        state.Step = "dayaction";
        state.WaitingText = false;
        await bot.AnswerCallbackQuery(cq.Id);
        await bot.SendMessage(chatId,
            "روز " + state.Weekday + " — چه کاری انجام شود؟",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ ویرایش زمان", "cs|set|" + state.Weekday),
                    InlineKeyboardButton.WithCallbackData("🗑️ حذف این روز", "cs|del|" + state.Weekday)
                }
            }));
    }

    static bool CanManageClass(long userId, string classCode)
    {
        if (IsAcademyAdmin(userId)) return true;
        return Teachers.TryGetValue(userId, out var classes) &&
               classes.Any(c => Eq(c.ClassCode, classCode));
    }

    static async Task HandleTeacherScheduleText(Message message, TeacherScheduleState state, string text)
    {
        long chatId = message.Chat.Id;
        if (state.Step == "weekday")
        {
            state.Weekday = text.Trim();
            state.Step = "time";
            state.WaitingText = true;
            await Send(chatId, "⏰ بازه زمانی را بنویس (مثلاً: ۱۸:۰۰ تا ۲۰:۰۰).");
            return;
        }
        if (state.Step != "time") return;

        var m = Regex.Match(text, @"(\d{1,2}[:.]\d{2})\s*(?:-|–|تا)\s*(\d{1,2}[:.]\d{2})");
        if (!m.Success)
        {
            await Send(chatId, "زمان را این‌طوری بنویس: ۱۸:۰۰ تا ۲۰:۰۰");
            return;
        }
        state.StartTime = m.Groups[1].Value.Replace('.', ':');
        state.EndTime = m.Groups[2].Value.Replace('.', ':');
        state.WaitingText = false;

        var (ok, err) = await PostToAppsScriptWithError(state.Availability
            ? (object)new
            {
                action = "save_teacher_availability",
                id = state.EditId,
                teacher_id = string.IsNullOrEmpty(state.TargetTeacherId)
                    ? state.TeacherId.ToString(CultureInfo.InvariantCulture)
                    : state.TargetTeacherId,
                teacher_name = state.TeacherName,
                weekday = state.Weekday,
                start_time = state.StartTime,
                end_time = state.EndTime
            }
            : new
            {
                action = "save_class_schedule",
                class_code = state.ClassCode,
                weekday = state.Weekday,
                start_time = state.StartTime,
                end_time = state.EndTime,
                teacher_id = state.TeacherId.ToString(CultureInfo.InvariantCulture),
                teacher_name = state.TeacherName
            });
        TeacherScheduleStates.TryRemove(message.From!.Id, out _);
        await Send(chatId, ok ? "✅ ذخیره شد." : "❌ ذخیره نشد: " + err);
    }

    static async Task StartTeacherAvailability(Message message)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /availability را در چت خصوصی بزن.");
            return;
        }

        bool isAdmin = IsAcademyAdmin(userId);
        bool isTeacher = Teachers.TryGetValue(userId, out var classes) && classes.Count > 0;
        if (!isAdmin && !isTeacher)
        {
            await Send(chatId, "شما دسترسی ندارید.");
            return;
        }

        string filterTeacherId = isAdmin ? "" : userId.ToString(CultureInfo.InvariantCulture);
        if (isAdmin)
        {
            var teacherButtons = Teachers.Keys.Select(id =>
                new[] { InlineKeyboardButton.WithCallbackData("مدرس " + id, "ta|teacher|" + id) }).ToList();
            teacherButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("همه مدرس‌ها", "ta|teacher|all") });
            await bot.SendMessage(chatId, "زمان‌های کدام مدرس را می‌بینی؟",
                replyMarkup: new InlineKeyboardMarkup(teacherButtons));
            return;
        }

        await ShowTeacherAvailability(chatId, userId, filterTeacherId);
    }

    static async Task ShowTeacherAvailability(long chatId, long userId, string filterTeacherId, bool askAdd = true)
    {
        var res = await PostToAppsScriptWithResult(new
        {
            action = "list_teacher_availability",
            teacher_id = filterTeacherId
        });
        var sb = new StringBuilder("⏰ زمان‌های مدرس\n");
        var rows = new List<InlineKeyboardButton[]>();
        if (IsOk(res) && res!.Value.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                string id = JStr(it, "id");
                string teacherId = JStr(it, "teacher_id");
                string teacherName = JStr(it, "teacher_name");
                string weekday = JStr(it, "weekday");
                sb.Append("• " + teacherName + " | " + weekday + " " +
                    JStr(it, "start_time") + "–" + JStr(it, "end_time") + "\n");
                rows.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✏️ " + weekday, "ta|edit|" + teacherId + "|" + id),
                    InlineKeyboardButton.WithCallbackData("🗑️", "ta|del|" + teacherId + "|" + id)
                });
            }
        }
        if (rows.Count == 0) sb.Append("\nموردی ثبت نشده.\n");

        string targetId = filterTeacherId;
        if (string.IsNullOrEmpty(targetId) || !askAdd)
        {
            if (string.IsNullOrEmpty(targetId)) sb.Append("\nبرای افزودن، ابتدا یک مدرس را انتخاب کن.");
            await bot.SendMessage(chatId, sb.ToString(),
                replyMarkup: rows.Count > 0 ? new InlineKeyboardMarkup(rows) : null);
            return;
        }

        sb.Append("\nبرای افزودن: روز را انتخاب کن.");
        await bot.SendMessage(chatId, sb.ToString(),
            replyMarkup: rows.Count > 0 ? new InlineKeyboardMarkup(rows) : null);
        string targetName = "";
        if (long.TryParse(targetId, out var tid) && Teachers.TryGetValue(tid, out var tcs))
            targetName = tcs.FirstOrDefault()?.TeacherName ?? "";
        TeacherScheduleStates[userId] = new TeacherScheduleState
        {
            TeacherId = userId,
            TargetTeacherId = targetId,
            TeacherName = targetName,
            Availability = true,
            Step = "weekday"
        };
        await AskScheduleWeekday(chatId, "روز زمان آزاد را انتخاب کن:", "ta|day|");
    }

    static async Task HandleTeacherAvailabilityCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3) { await bot.AnswerCallbackQuery(cq.Id); return; }

        if (parts[1] == "teacher")
        {
            if (!IsAcademyAdmin(userId)) { await bot.AnswerCallbackQuery(cq.Id); return; }
            string filter = parts[2] == "all" ? "" : parts[2];
            await bot.AnswerCallbackQuery(cq.Id);
            await ShowTeacherAvailability(chatId, userId, filter);
            return;
        }

        if (parts[1] == "del")
        {
            string teacherId = parts.Length >= 4 ? parts[2] : userId.ToString(CultureInfo.InvariantCulture);
            string delId = parts.Length >= 4 ? parts[3] : parts[2];
            var (ok, err) = await PostToAppsScriptWithError(new
            {
                action = "delete_teacher_availability",
                teacher_id = teacherId,
                id = delId
            });
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, ok ? "✅ حذف شد." : "❌ حذف نشد: " + err);
            await ShowTeacherAvailability(chatId, userId, teacherId, false);
            return;
        }
        if (parts[1] == "edit")
        {
            if (parts.Length < 4) { await bot.AnswerCallbackQuery(cq.Id); return; }
            string teacherId = parts[2];
            string editId = parts[3];
            string weekday = "", start = "", end = "";
            var res = await PostToAppsScriptWithResult(new
            {
                action = "list_teacher_availability",
                teacher_id = teacherId
            });
            if (IsOk(res) && res!.Value.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (JStr(it, "id") != editId) continue;
                    weekday = JStr(it, "weekday");
                    start = JStr(it, "start_time");
                    end = JStr(it, "end_time");
                    break;
                }
            }
            if (string.IsNullOrEmpty(weekday)) { await bot.AnswerCallbackQuery(cq.Id, "مورد پیدا نشد."); return; }
            TeacherScheduleStates[userId] = new TeacherScheduleState
            {
                TeacherId = userId,
                TargetTeacherId = teacherId,
                EditId = editId,
                Weekday = weekday,
                StartTime = start,
                EndTime = end,
                Availability = true,
                Step = "time",
                WaitingText = true
            };
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "⏰ بازه زمانی جدید را بنویس (مثلاً: ۱۸:۰۰ تا ۲۰:۰۰).\nبرای لغو: /cancel");
            return;
        }

        if (!TeacherScheduleStates.TryGetValue(userId, out var state) || !state.Availability)
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده.");
            return;
        }

        if (parts[1] == "day")
        {
            state.Weekday = parts[2];
            state.Step = "time";
            state.WaitingText = true;
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "⏰ بازه زمانی را بنویس (مثلاً: ۱۸:۰۰ تا ۲۰:۰۰).\nبرای لغو: /cancel");
            return;
        }
        await bot.AnswerCallbackQuery(cq.Id);
    }

    // ----- اطلاع زمان تعیین سطح آنلاین (وقتی staff ستون interview_time را پر کند) -----

    static async Task InterviewLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                await CheckInterviews();
            }
            catch (Exception ex)
            {
                AppLog.Error("InterviewLoop", "exception", ex);
            }
        }
    }

    static async Task CheckInterviews()
    {
        var res = await PostToAppsScriptWithResult(new { action = "get_pending_interviews" });
        if (!IsOk(res) || !res!.Value.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        var okIds = new List<string>();
        var failIds = new List<string>();

        foreach (var it in arr.EnumerateArray())
        {
            string attemptId = JStr(it, "attempt_id");
            string when = JStr(it, "interview_time");
            if (attemptId == "" || when == "" || !long.TryParse(JStr(it, "telegram_id"), out long target)) continue;

            try
            {
                await bot.SendMessage(target,
                    "📅 زمان تعیین سطح آنلاین با مدرس:\n\n" + when +
                    "\n\nاین جلسه برای تعیین سطح نهایی است. اگر این زمان برات مناسب نیست، به آکادمی اطلاع بده.");
                okIds.Add(attemptId);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Interview", "notify failed: " + ex.Message, target);
                failIds.Add(attemptId);
            }
            await Task.Delay(100);
        }

        if (okIds.Count > 0)
            await PostToAppsScript(new { action = "mark_interview_notified", attempt_ids = okIds, status = "Yes" });

        if (failIds.Count > 0)
        {
            await PostToAppsScript(new { action = "mark_interview_notified", attempt_ids = failIds, status = "Failed" });
            foreach (var id in PlacementAdminIds.Distinct().ToList())
            {
                try { await bot.SendMessage(id, "⚠️ پیام زمان تعیین سطح به " + failIds.Count + " نفر نرسید (احتمالاً بات را بلاک کرده‌اند). ستون notified در شیت Placement_Results روی Failed است."); }
                catch (Exception ex) { AppLog.Warn(nameof(CheckInterviews), ex.Message); }
            }
        }
    }

    // ===================== HOMEWORK =====================

    static async Task StartHomework(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /homework را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        if (await BlockedByFeedbackGate(chatId, userId)) return;
        var student = await FindStudentFull(userId.ToString());

        if (student == null || string.IsNullOrWhiteSpace(student.ClassCode))
        {
            await Send(chatId, "اطلاعات کلاس شما پیدا نشد. Student_Info را چک کنید.");
            return;
        }

        var result = await PostToAppsScriptWithResult(new
        {
            action = "get_homework",
            class_code = student.ClassCode,
            student_id = student.StudentId
        });

        if (!result.HasValue)
        {
            await Send(chatId, "ارتباط با سرور برقرار نشد. چند دقیقه بعد دوباره امتحان کن.");
            return;
        }

        if (!IsOk(result) || !result.Value.TryGetProperty("homework", out var hwJson))
        {
            await Send(chatId, "فعلاً تکلیفی برای کلاس شما ثبت نشده.");
            return;
        }

        string hwText = JStr(hwJson, "homework_text");
        string unit = JStr(hwJson, "unit");
        string lesson = JStr(hwJson, "lesson");
        string due = JStr(hwJson, "due_lesson");
        int assigned = (int)(JNum(hwJson, "assigned_count") ?? 1);
        int submitted = (int)(JNum(hwJson, "submitted_count") ?? 0);
        int pendingRow = (int)(JNum(hwJson, "pending_row") ?? -1);
        bool lastGraded = JBool(hwJson, "last_graded");
        var editable = new List<(int Row, string Label)>();
        if (hwJson.TryGetProperty("editable", out var edArr) && edArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in edArr.EnumerateArray())
            {
                int row = (int)(JNum(e, "row") ?? 0);
                if (row <= 0) continue;
                string eu = JStr(e, "unit");
                string el = JStr(e, "lesson");
                string preview = JStr(e, "preview");
                string ul = eu + (string.IsNullOrWhiteSpace(el) ? "" : "_" + el);
                string label = "✏️ " + (ul == "" ? "تکلیف" : ul);
                if (!string.IsNullOrWhiteSpace(preview)) label += " — " + preview;
                if (label.Length > 60) label = label.Substring(0, 60);
                editable.Add((row, label));
            }
        }

        if (string.IsNullOrWhiteSpace(hwText))
        {
            await Send(chatId, "فعلاً تکلیفی برای کلاس شما ثبت نشده.");
            return;
        }

        HomeworkStates[userId] = new HomeworkState
        {
            StudentId = student.StudentId,
            StudentName = student.FullName,
            ClassCode = student.ClassCode,
            Unit = unit,
            Lesson = lesson,
            HomeworkText = hwText,
            WaitingSubmission = false,
            AssignedCount = assigned,
            SubmittedCount = submitted,
            PendingRow = pendingRow,
            LastGraded = lastGraded,
            EditableRows = new HashSet<int>(editable.Select(x => x.Row))
        };

        await SendLong(chatId,
            "📝 تکلیف کلاس " + student.ClassCode + "\n" +
            "درس: " + unit + (string.IsNullOrWhiteSpace(lesson) ? "" : "_" + lesson) + "\n" +
            (string.IsNullOrWhiteSpace(due) ? "" : "برای درس: " + due + "\n") +
            "\n" + hwText);

        var rows = new List<InlineKeyboardButton[]>();
        if (!lastGraded)
        {
            foreach (var e in editable)
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData(e.Label, "hw|ed|" + e.Row) });
        }
        if (submitted < assigned)
        {
            string sendLabel = (assigned > 1 && submitted >= 1) ? "📤 ارسال تکلیف بعدی" : "📤 ارسال تکلیف";
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(sendLabel, "hw|next") });
        }

        string status = "ارسال‌شده: " + submitted + " از " + assigned;
        if (lastGraded) status += "\nنمره ثبت شده؛ ویرایش قفل است.";

        if (rows.Count == 0)
        {
            await Send(chatId, status + "\nسهمیه تکلیف این جلسه پر شده است.");
            HomeworkStates.TryRemove(userId, out _);
            return;
        }

        await bot.SendMessage(chatId,
            status + "\nیکی از گزینه‌ها را انتخاب کن:\n(برای انصراف: /cancel)",
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task StartEditHomework(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /edithomework را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        if (await BlockedByFeedbackGate(chatId, userId)) return;
        var student = await FindStudentFull(userId.ToString());
        if (student == null || string.IsNullOrWhiteSpace(student.ClassCode))
        {
            await Send(chatId, "اطلاعات کلاس شما پیدا نشد. Student_Info را چک کنید.");
            return;
        }

        var result = await PostToAppsScriptWithResult(new
        {
            action = "get_homework",
            class_code = student.ClassCode,
            student_id = student.StudentId
        });

        if (!IsOk(result) || !result!.Value.TryGetProperty("homework", out var hwJson))
        {
            await Send(chatId, "تکلیفی برای ویرایش پیدا نشد.");
            return;
        }

        var editable = new List<(int Row, string Label)>();
        if (hwJson.TryGetProperty("editable", out var edArr) && edArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in edArr.EnumerateArray())
            {
                int row = (int)(JNum(e, "row") ?? 0);
                if (row <= 0) continue;
                string eu = JStr(e, "unit");
                string el = JStr(e, "lesson");
                string preview = JStr(e, "preview");
                string ul = eu + (string.IsNullOrWhiteSpace(el) ? "" : "_" + el);
                string label = "✏️ " + (ul == "" ? "تکلیف" : ul);
                if (!string.IsNullOrWhiteSpace(preview)) label += " — " + preview;
                if (label.Length > 60) label = label.Substring(0, 60);
                editable.Add((row, label));
            }
        }

        if (editable.Count == 0)
        {
            await Send(chatId, "تکلیف بدون نمره برای ویرایش وجود ندارد.");
            return;
        }

        HomeworkStates[userId] = new HomeworkState
        {
            StudentId = student.StudentId,
            StudentName = student.FullName,
            ClassCode = student.ClassCode,
            Unit = JStr(hwJson, "unit"),
            Lesson = JStr(hwJson, "lesson"),
            HomeworkText = JStr(hwJson, "homework_text"),
            WaitingSubmission = false,
            EditableRows = new HashSet<int>(editable.Select(x => x.Row))
        };

        var rows = editable.Select(e =>
            new[] { InlineKeyboardButton.WithCallbackData(e.Label, "hw|ed|" + e.Row) }).ToList();
        await bot.SendMessage(chatId, "کدام تکلیف را ویرایش می‌کنی؟ (فقط تکلیف‌های بدون نمره)",
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandleHomeworkCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 2) return;

        if (!HomeworkStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /homework بزن.");
            return;
        }

        if (parts[1] == "ed" && parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out int row) || !state.EditableRows.Contains(row))
            {
                await bot.AnswerCallbackQuery(cq.Id, "این تکلیف قابل ویرایش نیست.");
                return;
            }
            state.Mode = "edit";
            state.EditRow = row;
            state.WaitingSubmission = true;
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "جواب جدید تکلیف را بفرست (متن، ویس، عکس یا فایل).\nبرای انصراف: /cancel");
            return;
        }

        if (parts[1] == "edit")
        {
            if (state.PendingRow <= 0 || state.LastGraded)
            {
                await bot.AnswerCallbackQuery(cq.Id, "این تکلیف قابل ویرایش نیست.");
                return;
            }
            state.Mode = "edit";
            state.EditRow = state.PendingRow;
            state.WaitingSubmission = true;
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "جواب جدید تکلیف را بفرست (متن، ویس، عکس یا فایل).\nبرای انصراف: /cancel");
            return;
        }

        if (parts[1] == "next")
        {
            if (state.SubmittedCount >= state.AssignedCount)
            {
                await bot.AnswerCallbackQuery(cq.Id, "سهمیه تکلیف این جلسه پر شده است.");
                return;
            }
            state.Mode = "next";
            state.WaitingSubmission = true;
            await bot.AnswerCallbackQuery(cq.Id);
            string nextWord = (state.AssignedCount > 1 && state.SubmittedCount >= 1) ? " بعدی" : "";
            await Send(chatId, "جواب تکلیف" + nextWord + " را بفرست (متن، ویس، عکس یا فایل).\nبرای انصراف: /cancel");
            return;
        }

        await bot.AnswerCallbackQuery(cq.Id);
    }

    static async Task HandleHomeworkSubmission(Message message, HomeworkState state)
    {
        string text = (message.Text ?? "").Trim();
        long chatId = message.Chat.Id;

        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("/"))
        {
            await Send(chatId, "لطفاً جواب تکلیف را بفرست (متن، ویس، عکس یا فایل) یا /cancel بزن.");
            return;
        }

        if (text.Length > 3000)
        {
            await Send(chatId, "متن خیلی طولانیه (حداکثر ۳۰۰۰ کاراکتر). کوتاه‌ترش کن و دوباره بفرست.");
            return;
        }

        await SubmitHomework(message, state, text, "text", null, text);
    }

    static bool HasHomeworkMedia(Message m)
    {
        return m.Voice != null || m.Audio != null || m.Document != null ||
               (m.Photo != null && m.Photo.Length > 0) || m.VideoNote != null;
    }

    static async Task HandleHomeworkMedia(Message message, HomeworkState state)
    {
        string kind;
        string fileId;

        if (message.Voice != null) { kind = "voice"; fileId = message.Voice.FileId; }
        else if (message.Audio != null) { kind = "audio"; fileId = message.Audio.FileId; }
        else if (message.Document != null) { kind = "document"; fileId = message.Document.FileId; }
        else if (message.Photo != null && message.Photo.Length > 0) { kind = "photo"; fileId = message.Photo[message.Photo.Length - 1].FileId; }
        else if (message.VideoNote != null) { kind = "videonote"; fileId = message.VideoNote.FileId; }
        else return;

        string caption = (message.Caption ?? "").Trim();
        if (caption.Length > 800) caption = caption.Substring(0, 800);

        // تو شیت: نوع + file_id (+ کپشن)
        string submission = "[" + kind + "] file_id=" + fileId + (caption.Length > 0 ? " | caption: " + caption : "");
        await SubmitHomework(message, state, submission, kind, fileId, caption);
    }

    // ذخیره تو شیت، و بعد از ذخیره موفق: ارسال به گروه کلاس
    static async Task SubmitHomework(Message message, HomeworkState state, string submission,
        string kind, string? fileId, string textOrCaption)
    {
        long userId = message.From!.Id;
        long chatId = message.Chat.Id;

        // جلوگیری از ثبت دوباره با ارسال سریع
        lock (state)
        {
            if (!state.WaitingSubmission) return;
            state.WaitingSubmission = false;
        }

        bool isEdit = state.Mode == "edit";
        object payload = isEdit
            ? (object)new
            {
                action = "edit_homework",
                student_id = state.StudentId,
                student_name = state.StudentName,
                class_code = state.ClassCode,
                unit = state.Unit,
                lesson = state.Lesson,
                submission = submission,
                row = state.EditRow,
                date = TodayStr()
            }
            : (object)new
            {
                action = "submit_homework",
                term_hint = TermFor(state.ClassCode),
                student_id = state.StudentId,
                student_name = state.StudentName,
                class_code = state.ClassCode,
                unit = state.Unit,
                lesson = state.Lesson,
                homework_text = state.HomeworkText,
                submission = submission,
                date = TodayStr()
            };

        var (saveRes, saveErrDetail) = await PostToAppsScriptDetailed(payload);
        bool ok = IsOk(saveRes);
        string saveError = "";
        if (!ok)
        {
            if (saveRes.HasValue)
            {
                string se = JStr(saveRes.Value, "error");
                saveError = string.IsNullOrWhiteSpace(se) ? "پاسخ سرور ok نبود" : se;
            }
            else saveError = saveErrDetail;
        }
        if (!ok)
        {
            state.WaitingSubmission = true;
            AppLog.Error("SubmitHomework", (isEdit ? "edit_homework" : "submit_homework") +
                " failed: " + saveError + ", class=" + state.ClassCode, null, userId);
            if (saveError == "assigned_limit")
                await Send(chatId, "سهمیه تکلیف این جلسه پر شده است.");
            else if (saveError == "graded")
                await Send(chatId, "این تکلیف نمره گرفته و قابل ویرایش نیست.");
            else
                await Send(chatId, "❌ ثبت تکلیف انجام نشد (مشکل سرور). دوباره بفرست یا /cancel بزن.");
            return;
        }

        // ویرایش: پیام قدیمی گروه (اگر ثبت شده) پاک شود تا نسخه جدید جای آن بیاید.
        if (isEdit && saveRes.HasValue)
        {
            await TryDeleteOldGroupHomeworkMessage(
                JStr(saveRes.Value, "old_group_chat_id"),
                JStr(saveRes.Value, "old_group_message_id"),
                state);
        }

        HomeworkStates.TryRemove(userId, out _);
        await Send(chatId, isEdit ? "✅ تکلیف شما ویرایش شد." : "✅ تکلیف شما ثبت شد. ممنون!");
        RecomputeInBackground(state.ClassCode, "SubmitHomework.Recompute");

        string result = await PostHomeworkToGroup(state, kind, fileId, textOrCaption);
        AppLog.Info("SubmitHomework.Group", "post result=" + result + " class=" + state.ClassCode +
            " edit=" + isEdit, ParseUserId(state.StudentId));
        if (result != "ok")
        {
            // گروه تنظیم نشده یا ارسال به گروه شکست خورده: تکلیف را به چت خصوصی مدرس کلاس بفرست
            bool toTeacher = await PostHomeworkToTeachers(state, kind, fileId, textOrCaption);
            if (!toTeacher && result == "failed")
                await Send(chatId, "⚠️ تکلیف تو شیت ذخیره شد، ولی ارسال به گروه کلاس ناموفق بود. مدرس از شیت هم می‌تونه ببینه.");
        }
    }

    static string KindLabel(string kind)
    {
        switch (kind)
        {
            case "text": return "متن";
            case "voice": return "ویس";
            case "audio": return "فایل صوتی";
            case "document": return "فایل";
            case "photo": return "عکس";
            case "videonote": return "ویدیو";
            default: return kind;
        }
    }

    // نتیجه: "ok" | "nogroup" (گروه تنظیم نشده؛ فقط لاگ) | "failed"
    // ارسال یک تکلیف به یک چت (گروه یا مدرس). اگر خطا بشه exception می‌ده.
    static async Task<int?> SendHomeworkPost(long targetChatId, HomeworkState state, string kind, string? fileId, string textOrCaption)
    {
        string name = string.IsNullOrWhiteSpace(state.StudentName) ? state.StudentId : state.StudentName;
        string lesson = state.Unit + (string.IsNullOrWhiteSpace(state.Lesson) ? "" : "_" + state.Lesson);
        string header =
            "📝 تکلیف جدید\n" +
            "زبان‌آموز: " + name + "\n" +
            "کلاس: " + state.ClassCode + "\n" +
            "درس: " + lesson + "\n" +
            "نوع: " + KindLabel(kind);

        if (kind == "text")
        {
            var sentText = await bot.SendMessage(targetChatId, header + "\n\n" + textOrCaption);
            return sentText.MessageId;
        }

        string caption = header + (string.IsNullOrWhiteSpace(textOrCaption) ? "" : "\n\n" + textOrCaption);
        if (caption.Length > 1000) caption = caption.Substring(0, 1000);
        var file = InputFile.FromFileId(fileId ?? "");

        switch (kind)
        {
            case "voice":
                {
                    var sentHw = await bot.SendVoice(targetChatId, file, caption: caption);
                    return sentHw.MessageId;
                }
            case "audio":
                {
                    var sentHw = await bot.SendAudio(targetChatId, file, caption: caption);
                    return sentHw.MessageId;
                }
            case "document":
                {
                    var sentHw = await bot.SendDocument(targetChatId, file, caption: caption);
                    return sentHw.MessageId;
                }
            case "photo":
                {
                    var sentHw = await bot.SendPhoto(targetChatId, file, caption: caption);
                    return sentHw.MessageId;
                }
            case "videonote":
                {
                    var sentHw = await bot.SendVideoNote(targetChatId, file);
                    try { await Send(targetChatId, header); } catch (Exception ex) { AppLog.Warn(nameof(SendHomeworkPost), ex.Message); }
                    return sentHw.MessageId;
                }
            default:
                {
                    var sentHw = await bot.SendMessage(targetChatId, header);
                    return sentHw.MessageId;
                }
        }
    }

    // نتیجه: "ok" | "nogroup" (گروه تنظیم نشده) | "failed"
    // پاک کردن پیام قدیمی تکلیف در گروه هنگام ویرایش (اگر ثبت شده باشد).
    // شکست حذف، مانع ارسال نسخه جدید نمی‌شود؛ فقط WARN لاگ می‌شود.
    static async Task TryDeleteOldGroupHomeworkMessage(string oldChatId, string oldMessageId, HomeworkState state)
    {
        if (string.IsNullOrWhiteSpace(oldChatId) || string.IsNullOrWhiteSpace(oldMessageId)) return;
        if (!long.TryParse(oldChatId.Trim(), out long chatId) || chatId == 0) return;
        if (!int.TryParse(oldMessageId.Trim(), out int messageId) || messageId == 0) return;
        try
        {
            await bot.DeleteMessage(chatId, messageId);
            AppLog.Info("HomeworkEdit.Delete", "deleted old group msg=" + messageId + " class=" + state.ClassCode,
                ParseUserId(state.StudentId));
        }
        catch (Exception ex)
        {
            AppLog.Warn("HomeworkEdit.Delete", "delete old group msg failed (continuing): " + ex.Message +
                " class=" + state.ClassCode + " msg=" + messageId, ParseUserId(state.StudentId));
        }
    }

    // نتیجه: "ok" | "nogroup" (گروه تنظیم نشده) | "failed"
    static async Task<string> PostHomeworkToGroup(HomeworkState state, string kind, string? fileId, string textOrCaption)
    {
        long groupId = GetClassGroupId(state.ClassCode);
        if (groupId == 0)
        {
            // شاید رجیستری شیت بعد از استارت آپ‌دیت شده باشد؛ یک بار دیگر بخوان.
            await LoadClassRegistryFromServer();
            groupId = GetClassGroupId(state.ClassCode);
        }
        if (groupId == 0)
        {
            AppLog.Warn("SubmitHomework", "group chat id not set for class " + state.ClassCode, ParseUserId(state.StudentId));
            return "nogroup";
        }
        try
        {
            int? msgId = await SendHomeworkPost(groupId, state, kind, fileId, textOrCaption);
            if (msgId != null)
            {
                var attach = new
                {
                    action = "grade_homework_attach",
                    student_id = state.StudentId,
                    class_code = state.ClassCode,
                    group_chat_id = groupId.ToString(),
                    message_id = msgId.Value.ToString(),
                    row = state.Mode == "edit" ? state.EditRow : 0
                };
                var (attached, attachErr) = await PostToAppsScriptWithError(attach);
                if (attached)
                    AppLog.Info("HomeworkAttach", "class=" + state.ClassCode + " msg=" + msgId.Value, ParseUserId(state.StudentId));
                else
                    AppLog.Warn("HomeworkAttach", "attach failed: " + attachErr + " class=" + state.ClassCode, ParseUserId(state.StudentId));
            }
            AppLog.Info("SubmitHomework.Group", "posted homework to group " + groupId +
                " msg=" + (msgId?.ToString() ?? "-") + " class=" + state.ClassCode,
                ParseUserId(state.StudentId));
            return "ok";
        }
        catch (Exception ex)
        {
            AppLog.Error("SubmitHomework.Group", "post to class group failed, class=" + state.ClassCode + " kind=" + kind, ex, ParseUserId(state.StudentId));
            return "failed";
        }
    }

    // تکلیف را به چت خصوصی مدرس(های) همین کلاس می‌فرستد (وقتی گروه تنظیم نیست یا ارسال به گروه شکست خورده).
    // true یعنی حداقل به یک مدرس رسید.
    static async Task<bool> PostHomeworkToTeachers(HomeworkState state, string kind, string? fileId, string textOrCaption)
    {
        var teacherIds = Teachers
            .Where(kv => kv.Value.Any(c => string.Equals(c.ClassCode, state.ClassCode, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();

        if (teacherIds.Count == 0)
        {
            AppLog.Warn("SubmitHomework", "no teacher found for class " + state.ClassCode, ParseUserId(state.StudentId));
            return false;
        }

        bool any = false;
        foreach (var tid in teacherIds)
        {
            try
            {
                await SendHomeworkPost(tid, state, kind, fileId, textOrCaption);
                any = true;
            }
            catch (Exception ex)
            {
                AppLog.Error("SubmitHomework.Teacher", "post to teacher failed, class=" + state.ClassCode + " kind=" + kind, ex, ParseUserId(state.StudentId));
            }
        }
        return any;
    }

    // ===================== CLASS DONE =====================

    static async Task StartClassDone(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /classdone را در چت خصوصی بزن.");
            return;
        }

        long teacherId = message.From!.Id;
        if (!Teachers.TryGetValue(teacherId, out var classes))
        {
            await Send(chatId, "شما به‌عنوان مدرس ثبت نشده‌اید.\nآیدی تلگرام شما: " + teacherId);
            return;
        }

        if (classes.Count == 1)
        {
            await BeginClassSession(teacherId, chatId, classes[0]);
            return;
        }

        var buttons = classes.Select(c =>
            new[] { InlineKeyboardButton.WithCallbackData(c.ClassCode, "cd|class|" + c.ClassCode) }
        ).ToList();

        await bot.SendMessage(chatId, "کدام کلاس را برگزار کردید؟",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task BeginClassSession(long teacherId, long chatId, TeacherClass tc)
    {
        var students = await LoadStudentsByClass(tc.ClassCode);
        // زبان‌آموزهایی که فقط از پیام گروه شناخته شده‌اند (هنوز در Student_Info/CSV نیستند) هم بیان
        MergeGroupStudents(students, tc.ClassCode);

        var term = await GetTermStructure(tc.ClassCode);

        // سطح: اول ساختار ترم فعال (منبع حقیقت)، بعد رجیستری، بعد Teachers
        string level = term != null && !string.IsNullOrWhiteSpace(term.Level) ? term.Level : GetClassLevel(tc.ClassCode);
        if (string.IsNullOrWhiteSpace(level)) level = tc.Level;

        ClassDoneStates[teacherId] = new ClassDoneState
        {
            TeacherTelegramId = teacherId,
            ClassCode = tc.ClassCode,
            TeacherName = tc.TeacherName,
            Level = level,
            Students = students,
            Date = TodayStr(),
            Stage = "lesson"
        };

        var rows = new List<InlineKeyboardButton[]>();
        if (term != null && term.Active && term.Sessions.Count > 0)
        {
            var lessons = term.Sessions;
            for (int i = 0; i < lessons.Count; i += LessonButtonsPerRow)
                rows.Add(lessons.Skip(i).Take(LessonButtonsPerRow)
                    .Select(ul => InlineKeyboardButton.WithCallbackData(ul, "cd|lesson|" + ul)).ToArray());
        }

        // دکمه‌های شروع و پایان ترم (به‌جای وارد کردن دستی تاریخ تو شیت)
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🟢 شروع ترم جدید", "cd|term|start"),
            InlineKeyboardButton.WithCallbackData("🔴 پایان ترم", "cd|term|end"),
        });
        string termLine = TermLine(term);
        string prompt = (term != null && term.Active && term.Sessions.Count > 0)
            ? "کدام جلسه امروز برگزار شد؟"
            : "برای ثبت جلسه، اول «🟢 شروع ترم جدید» را بزن تا ساختار ترم ساخته شود.";

        await bot.SendMessage(chatId,
            "کلاس: " + tc.ClassCode + "\n" + termLine + "\n" + prompt,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandleClassDoneCallback(CallbackQuery cq)
    {
        if (cq.Message == null || cq.Data == null) return;
        long teacherId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3) return;

        string action = parts[1];
        string value = parts[2];

        if (!Teachers.TryGetValue(teacherId, out var myClasses))
        {
            await bot.AnswerCallbackQuery(cq.Id, "شما به‌عنوان مدرس ثبت نشده‌اید.");
            return;
        }

        if (action == "class")
        {
            var tc = myClasses.FirstOrDefault(x => x.ClassCode == value);
            if (tc == null) { await bot.AnswerCallbackQuery(cq.Id); return; }
            await bot.AnswerCallbackQuery(cq.Id);
            await BeginClassSession(teacherId, chatId, tc);
            return;
        }

        if (!ClassDoneStates.TryGetValue(teacherId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /classdone بزن.");
            return;
        }

        await state.Gate.WaitAsync();
        try
        {
            if (action == "tset")
            {
                await bot.AnswerCallbackQuery(cq.Id);
                if (parts.Length < 3)
                {
                    await Send(chatId, "این دکمه قدیمی است. دوباره «🟢 شروع ترم جدید» را بزن.");
                    return;
                }

                string arg = parts.Length >= 4 ? parts[3] : "";

                // «تأیید روزها» عمداً ۳ بخشی است (cd|tset|daysdone) و نباید با گارد ۴ بخشی رد شود.
                if (value == "daysdone" && state.TermSetupStep == "weekdays")
                {
                    if (string.IsNullOrWhiteSpace(state.TermWeekdays))
                    {
                        await Send(chatId, "حداقل یک روز کلاس را انتخاب کن.");
                        return;
                    }
                    state.TermSetupStep = "startdate";
                    state.WaitingText = false;
                    state.TextMode = "";
                    await Send(chatId,
                        "📅 تاریخ شروع ترم را بنویس (مثلاً 2026-03-01).\nبرای لغو: /cancel");
                    return;
                }

                // ورود دستی بازه زمانی ترم (به‌جای تکیه صرف روی دکمه‌های آماده)
                if (value == "timecustom" && state.TermSetupStep == "daytime")
                {
                    state.TextMode = "termtime";
                    await Send(chatId,
                        "⏰ بازه زمانی " + TermSelectedDays(state).ElementAtOrDefault(state.TermDayIndex) +
                        " را بنویس (مثلاً: 16:30-18:00 یا ۱۶:۳۰ تا ۱۸:۰۰).\nبرای لغو: /cancel");
                    return;
                }

                if (parts.Length < 4)
                {
                    await Send(chatId, "این دکمه قدیمی است. دوباره «🟢 شروع ترم جدید» را بزن.");
                    return;
                }

                if (value == "ctype" && state.TermSetupStep == "classtype")
                {
                    if (arg != "private" && arg != "public")
                    {
                        await Send(chatId, "نوع کلاس نامعتبر است.");
                        return;
                    }
                    state.TermClassType = arg;
                    state.TermSetupStep = "package";
                    var packages = new[] { "کتاب‌محور", "Talk", "Story Mode", "Discussion" };
                    var rows = packages.Select(p => new[]
                    {
                        InlineKeyboardButton.WithCallbackData(p, "cd|tset|pkg|" + p)
                    }).ToList();
                    await bot.SendMessage(chatId, "نوع پکیج دوره این ترم چیست؟",
                        replyMarkup: new InlineKeyboardMarkup(rows));
                    return;
                }

                if (value == "pkg" && state.TermSetupStep == "package")
                {
                    if (arg != "کتاب‌محور" && arg != "Talk" && arg != "Story Mode" && arg != "Discussion")
                    {
                        await Send(chatId, "پکیج نامعتبر است.");
                        return;
                    }
                    state.TermPackage = arg;
                    state.TermSetupStep = "level";
                    await bot.SendMessage(chatId, "سطح (Level) را انتخاب کن:",
                        replyMarkup: TermLevelButtons());
                    return;
                }

                if (value == "lvl" && state.TermSetupStep == "level")
                {
                    if (!int.TryParse(arg, out int level) || level < 1 || level > 6)
                    {
                        await Send(chatId, "سطح نامعتبر است.");
                        return;
                    }
                    state.TermLevel = level.ToString(CultureInfo.InvariantCulture);
                    state.TermSetupStep = "sub";
                    await bot.SendMessage(chatId, "زیرسطح را انتخاب کن (مثلاً 204):",
                        replyMarkup: TermSubLevelButtons(level));
                    return;
                }

                if (value == "sub" && state.TermSetupStep == "sub")
                {
                    var mapped = MapSubLevelToUnits(arg);
                    if (mapped.Book == 0)
                    {
                        await Send(chatId, "زیرسطح نامعتبر است.");
                        return;
                    }
                    state.TermLevel = arg;
                    state.TermBook = mapped.Book.ToString(CultureInfo.InvariantCulture);
                    state.TermUnit1 = mapped.Unit1.ToString(CultureInfo.InvariantCulture);
                    state.TermUnit2 = mapped.Unit2.ToString(CultureInfo.InvariantCulture);
                    state.TermSetupStep = "weekdays";
                    await bot.SendMessage(chatId,
                        "Evolve " + state.TermBook + " | یونیت‌های " + state.TermUnit1 + " و " + state.TermUnit2 +
                        "\nروزهای کلاس را انتخاب کن (چند روز مجاز است):",
                        replyMarkup: TermWeekdayButtons(state.TermWeekdays));
                    return;
                }

                if (value == "day" && state.TermSetupStep == "weekdays")
                {
                    var picked = state.TermWeekdays.Split(new[] { '،', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    if (picked.Contains(arg)) picked.Remove(arg); else picked.Add(arg);
                    state.TermWeekdays = string.Join("، ", PersianWeekdays.Where(d => picked.Contains(d)));
                    await bot.SendMessage(chatId, "روزهای کلاس را انتخاب کن (چند روز مجاز است):",
                        replyMarkup: TermWeekdayButtons(state.TermWeekdays));
                    return;
                }

                if (value == "time" && state.TermSetupStep == "daytime")
                {
                    if (!TryParseTimeRange(arg, out string tStart, out string tEnd))
                    {
                        await Send(chatId, "زمان نامعتبر است. مثلاً: 16:30-18:00");
                        return;
                    }
                    await SaveTermDayTimeAndContinue(chatId, state, tStart, tEnd);
                    return;
                }

                await Send(chatId, "این دکمه قدیمی است. دوباره «🟢 شروع ترم جدید» را بزن.");
                return;
            }

            if (action == "term")
            {
                await bot.AnswerCallbackQuery(cq.Id);
                if (state.Stage != "lesson")
                {
                    await Send(chatId, "دکمه‌های شروع و پایان ترم فقط ابتدای ثبت جلسه (قبل از انتخاب درس) فعال هستند.");
                    return;
                }
                await HandleTermButton(chatId, state, value);
                return;
            }

            if (action == "lesson")
            {
                if (state.Stage != "lesson")
                {
                    await bot.AnswerCallbackQuery(cq.Id, "درس قبلاً انتخاب شده.");
                    return;
                }
                var ul = value.Split('_');
                state.Unit = ul.Length > 0 ? ul[0] : value;
                state.Lesson = ul.Length > 1 ? ul[1] : "1";
                state.UnitLesson = value;
                state.Stage = "attendance";
                await bot.AnswerCallbackQuery(cq.Id, "درس " + value);
                await Send(chatId, "درس انتخاب شد: " + value + "\nحالا حضور و غیاب:");
                await AskNextStudentAttendance(chatId, state);
                return;
            }

            if (action == "att")
            {
                if (parts.Length < 4) { await bot.AnswerCallbackQuery(cq.Id); return; }
                string status = parts[3];
                if (state.Stage != "attendance" ||
                    !int.TryParse(value, out int idx) || idx != state.StudentIndex ||
                    (status != "Present" && status != "Absent"))
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                    return;
                }

                state.Students[state.StudentIndex].Status = status;
                state.StudentIndex++;
                await bot.AnswerCallbackQuery(cq.Id, status == "Present" ? "حاضر" : "غایب");
                await AskNextStudentAttendance(chatId, state);
                return;
            }

            if (action == "part")
            {
                if (state.Stage != "part") { await bot.AnswerCallbackQuery(cq.Id, "قبلاً ثبت شده."); return; }
                state.Participation = value;
                state.Stage = "env";
                await bot.AnswerCallbackQuery(cq.Id);
                await AskEnvironment(chatId, state);
                return;
            }

            if (action == "env")
            {
                if (state.Stage != "env") { await bot.AnswerCallbackQuery(cq.Id, "قبلاً ثبت شده."); return; }
                state.Environment = value;
                state.Stage = "text";
                state.WaitingText = true;
                state.TextMode = "note";
                await bot.AnswerCallbackQuery(cq.Id);
                await Send(chatId, "نکته مهم جلسه را بنویس.\nاگر نکته‌ای نیست: /skip\nبرای لغو: /cancel");
                return;
            }

            if (action == "mst")
            {
                if (state.Stage != "mistakes")
                {
                    await bot.AnswerCallbackQuery(cq.Id, "این مرحله تمام شده.");
                    return;
                }

                if (value == "done")
                {
                    state.WaitingText = false;
                    state.TextMode = "";
                    state.Stage = "part";
                    await bot.AnswerCallbackQuery(cq.Id);
                    await AskParticipation(chatId, state);
                    return;
                }

                if (!int.TryParse(value, out int si) || si < 0 || si >= state.Students.Count ||
                    state.Students[si].Status != "Present")
                {
                    await bot.AnswerCallbackQuery(cq.Id);
                    return;
                }

                state.MistakeStudentIndex = si;
                state.WaitingText = true;
                state.TextMode = "mistake";
                await bot.AnswerCallbackQuery(cq.Id);

                var ms = state.Students[si];
                string mname = string.IsNullOrWhiteSpace(ms.Name) ? ms.StudentId : ms.Name;
                await Send(chatId,
                    "اشتباهات «" + mname + "» را بنویس. هر خط یک اشتباه:\n\n" +
                    "جمله اشتباه => جمله درست\n\n" +
                    "مثال:\nI goed to school => I went to school\n\n" +
                    "برای برگشت به لیست: /skip");
                return;
            }

            if (action == "retry")
            {
                if (value == "att" && state.Stage == "save_att")
                {
                    await bot.AnswerCallbackQuery(cq.Id);
                    await SaveAttendanceThenFeedback(chatId, state);
                    return;
                }
                if (value == "fin" && state.Stage == "save_fin")
                {
                    await bot.AnswerCallbackQuery(cq.Id);
                    await FinalizeClassDone(chatId, state);
                    return;
                }
                await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                return;
            }

            await bot.AnswerCallbackQuery(cq.Id);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task AskNextStudentAttendance(long chatId, ClassDoneState state)
    {
        if (state.Students.Count == 0)
        {
            await Send(chatId, "برای کلاس " + state.ClassCode + " هیچ زبان‌آموزی پیدا نشد.");
            await SaveAttendanceThenFeedback(chatId, state);
            return;
        }

        if (state.StudentIndex >= state.Students.Count)
        {
            await SaveAttendanceThenFeedback(chatId, state);
            return;
        }

        var s = state.Students[state.StudentIndex];
        string name = string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name;
        string idx = state.StudentIndex.ToString();
        var buttons = new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ حاضر", "cd|att|" + idx + "|Present"),
                InlineKeyboardButton.WithCallbackData("❌ غایب", "cd|att|" + idx + "|Absent"),
            }
        };

        await bot.SendMessage(chatId,
            "زبان‌آموز " + (state.StudentIndex + 1) + " از " + state.Students.Count + ":\n" + name,
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static InlineKeyboardMarkup RetryButton(string what)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🔁 تلاش دوباره", "cd|retry|" + what) }
        });
    }

    static async Task SaveAttendanceThenFeedback(long chatId, ClassDoneState state)
    {
        state.Stage = "save_att";
        await Send(chatId, "در حال ذخیره حضور و غیاب...");

        bool ok = await PostToAppsScript(new
        {
            action = "log_attendance",
            term_hint = TermFor(state.ClassCode),
            class_code = state.ClassCode,
            level = state.Level,
            unit = state.Unit,
            lesson = state.Lesson,
            teacher_name = state.TeacherName,
            date = state.Date,
            students = state.Students.Select(s => new
            {
                student_id = s.StudentId,
                name = s.Name,
                status = s.Status
            }).ToList()
        });

        if (!ok)
        {
            AppLog.Error("ClassDone.Attendance", "log_attendance failed, class=" + state.ClassCode, null, state.TeacherTelegramId);
            await bot.SendMessage(chatId,
                "❌ ذخیره حضور و غیاب انجام نشد (مشکل سرور).",
                replyMarkup: RetryButton("att"));
            return;
        }

        int present = state.Students.Count(x => x.Status == "Present");
        int absent = state.Students.Count(x => x.Status == "Absent");

        await Send(chatId,
            "✅ حضور و غیاب ثبت شد\n" +
            "کلاس: " + state.ClassCode + "\n" +
            "درس: " + state.UnitLesson + "\n" +
            "حاضر: " + present + "\n" +
            "غایب: " + absent + "\n\n" +
            "حالا فیدبک کلی کلاس:");

        state.Stage = "mistakes";
        await AskMistakeStudents(chatId, state);
    }

    // ----- ثبت اشتباهات زبان‌آموزها توسط مدرس -----

    // جداکننده‌های قابل قبول بین جمله غلط و درست
    private static readonly Regex MistakeSplit = new Regex(@"\s*(?:=>|->|→|\|)\s*");

    static List<MistakeItem> ParseMistakeLines(string text)
    {
        var list = new List<MistakeItem>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = MistakeSplit.Split(line, 2);
            if (parts.Length < 2) continue;

            string wrong = parts[0].Trim();
            string correct = parts[1].Trim();
            if (wrong.Length == 0 || correct.Length == 0) continue;
            if (wrong.Length > 300 || correct.Length > 300) continue;
            if (NormalizeAnswer(wrong) == NormalizeAnswer(correct)) continue; // فرقی ندارن

            list.Add(new MistakeItem { Wrong = wrong, Correct = correct });
        }
        return list;
    }

    static async Task AskMistakeStudents(long chatId, ClassDoneState state)
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < state.Students.Count; i++)
        {
            var s = state.Students[i];
            if (s.Status != "Present") continue;

            string name = string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name;
            state.MistakeCounts.TryGetValue(i, out int cnt);
            string label = cnt > 0 ? name + "  ✓" + cnt : name;
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, "cd|mst|" + i) });
        }

        // کسی حاضر نبود: مستقیم برو مرحله بعد
        if (rows.Count == 0)
        {
            state.Stage = "part";
            await AskParticipation(chatId, state);
            return;
        }

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ پایان / رد کردن", "cd|mst|done") });

        await bot.SendMessage(chatId,
            "✍️ اشتباهات زبان‌آموزها (اختیاری)\n" +
            "یک نفر را انتخاب کن و اشتباهاتش را بنویس. وقتی تمام شد «پایان» را بزن.",
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandleMistakeText(long chatId, ClassDoneState state, string text, bool skip)
    {
        if (skip)
        {
            state.WaitingText = false;
            state.TextMode = "";
            await AskMistakeStudents(chatId, state);
            return;
        }

        int si = state.MistakeStudentIndex;
        if (si < 0 || si >= state.Students.Count)
        {
            state.WaitingText = false;
            state.TextMode = "";
            await AskMistakeStudents(chatId, state);
            return;
        }

        var items = ParseMistakeLines(text);
        if (items.Count == 0)
        {
            await Send(chatId,
                "فرمت درست نبود. هر خط این‌طوری باشه:\n" +
                "جمله اشتباه => جمله درست\n\n" +
                "(یا /skip برای برگشت)");
            return;
        }

        var st = state.Students[si];
        string name = string.IsNullOrWhiteSpace(st.Name) ? st.StudentId : st.Name;

        // همین‌جا ذخیره می‌کنیم تا اگر مدرس وسط کار رها کرد چیزی از دست نره
        var (ok, saveError) = await PostToAppsScriptWithError(new
        {
            action = "log_teacher_mistakes",
            date = state.Date,
            class_code = state.ClassCode,
            unit = state.Unit,
            lesson = state.Lesson,
            teacher_name = state.TeacherName,
            items = items.Select(x => new
            {
                student_id = st.StudentId,
                student_name = st.Name,
                wrong = x.Wrong,
                correct = x.Correct
            }).ToList()
        });

        if (!ok)
        {
            AppLog.Error("ClassDone.TeacherMistakes", "log_teacher_mistakes failed: " + saveError, null, state.TeacherTelegramId);
            string shortErr = saveError.Length > 200 ? saveError.Substring(0, 200) : saveError;
            await Send(chatId, "❌ ذخیره نشد.\nدلیل: " + shortErr + "\n\nدوباره همان متن را بفرست یا /skip بزن.");
            return;
        }

        state.MistakeCounts.TryGetValue(si, out int old);
        state.MistakeCounts[si] = old + items.Count;
        state.WaitingText = false;
        state.TextMode = "";

        await Send(chatId, "✅ " + items.Count + " اشتباه برای «" + name + "» ثبت شد.");
        await AskMistakeStudents(chatId, state);
    }

    static async Task AskParticipation(long chatId, ClassDoneState state)
    {
        var buttons = new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", "cd|part|1"),
                InlineKeyboardButton.WithCallbackData("2", "cd|part|2"),
                InlineKeyboardButton.WithCallbackData("3", "cd|part|3"),
                InlineKeyboardButton.WithCallbackData("4", "cd|part|4"),
                InlineKeyboardButton.WithCallbackData("5", "cd|part|5"),
            }
        };

        await bot.SendMessage(chatId,
            "مشارکت کلاس امروز چطور بود؟ (۱ ضعیف تا ۵ عالی)",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task AskEnvironment(long chatId, ClassDoneState state)
    {
        var buttons = new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", "cd|env|1"),
                InlineKeyboardButton.WithCallbackData("2", "cd|env|2"),
                InlineKeyboardButton.WithCallbackData("3", "cd|env|3"),
                InlineKeyboardButton.WithCallbackData("4", "cd|env|4"),
                InlineKeyboardButton.WithCallbackData("5", "cd|env|5"),
            }
        };

        await bot.SendMessage(chatId,
            "فضای کلاس چطور بود؟ (۱ ضعیف تا ۵ عالی)",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task HandleClassDoneText(Message message, ClassDoneState state)
    {
        string text = (message.Text ?? "").Trim();
        long chatId = message.Chat.Id;
        bool skip = IsCommand(text, "/skip");

        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage == "mistakes" && state.TextMode == "mistake")
            {
                await HandleMistakeText(chatId, state, text, skip);
                return;
            }

            if (state.Stage != "text" || !state.WaitingText) return;

            if (state.TextMode == "note")
            {
                state.Note = skip ? "" : text;
                state.TextMode = "homework";
                await Send(chatId, "تکلیف جلسه بعد را بنویس.\nاگر بیش از یک تکلیف است، آخر متن x2 یا (2) بنویس.\nاگر تکلیفی نیست: /skip");
                return;
            }

            if (state.TextMode == "homework")
            {
                if (skip)
                {
                    state.HomeworkText = "";
                    state.AssignedCount = 1;
                }
                else
                {
                    var parsed = ParseHomeworkCount(text);
                    state.HomeworkText = parsed.Text;
                    state.AssignedCount = parsed.Count;
                }
                state.WaitingText = false;
                state.TextMode = "";
                state.Stage = "save_fin";
                await FinalizeClassDone(chatId, state);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // لیست شماره‌دار: هر آیتم با شماره و بعدش متن شروع می‌شود (مثلاً "1) ..." یا "2. ...")
    private static readonly Regex HomeworkNumberedList = new Regex(@"^\s*\d+[\.\)]\s+\S", RegexOptions.Multiline | RegexOptions.Compiled);

    static (string Text, int Count) ParseHomeworkCount(string text)
    {
        string raw = (text ?? "").Trim();
        string value = raw;
        int count = 1;
        bool hasMarker = false;

        var m = Regex.Match(value, @"^(?:[x×]\s*(\d{1,2})|\((\d{1,2})\))\s*(?:\r?\n|$)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            hasMarker = true;
            count = ParseHomeworkCountValue(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            value = value.Substring(m.Length).Trim();
        }
        else
        {
            m = Regex.Match(value, @"(?:^|\s)[x×]\s*(\d{1,2})\s*$|\((\d{1,2})\)\s*$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                hasMarker = true;
                count = ParseHomeworkCountValue(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                value = value.Substring(0, m.Index).Trim();
            }
        }

        if (!hasMarker)
        {
            var items = HomeworkNumberedList.Matches(value);
            if (items.Count >= 2) count = items.Count;
        }

        return (string.IsNullOrWhiteSpace(value) ? raw : value, count);
    }

    static int ParseHomeworkCountValue(string value)
    {
        if (!int.TryParse(value, out int count)) return 1;
        return Math.Clamp(count, 1, 50);
    }

    static async Task FinalizeClassDone(long chatId, ClassDoneState state)
    {
        state.Stage = "save_fin";
        await Send(chatId, "در حال ذخیره فیدبک و تکلیف...");

        string dueLesson = GetNextUnitLesson(state.UnitLesson);

        if (!state.FeedbackSaved)
        {
            bool ok1 = await PostToAppsScript(new
            {
                action = "log_class_feedback",
                term_hint = TermFor(state.ClassCode),
                date = state.Date,
                class_code = state.ClassCode,
                unit = state.Unit,
                lesson = state.Lesson,
                teacher_name = state.TeacherName,
                participation = state.Participation,
                environment = state.Environment,
                note = state.Note
            });

            if (!ok1)
            {
                AppLog.Error("ClassDone.Feedback", "log_class_feedback failed, class=" + state.ClassCode, null, state.TeacherTelegramId);
                await bot.SendMessage(chatId, "❌ ذخیره فیدبک انجام نشد (مشکل سرور).",
                    replyMarkup: RetryButton("fin"));
                return;
            }
            state.FeedbackSaved = true;
        }

        if (!state.HomeworkSaved && !string.IsNullOrWhiteSpace(state.HomeworkText))
        {
            bool ok2 = await PostToAppsScript(new
            {
                action = "log_homework",
                term_hint = TermFor(state.ClassCode),
                date = state.Date,
                class_code = state.ClassCode,
                unit = state.Unit,
                lesson = state.Lesson,
                teacher_name = state.TeacherName,
                homework_text = state.HomeworkText,
                due_lesson = dueLesson,
                assigned_count = state.AssignedCount
            });

            if (!ok2)
            {
                AppLog.Error("ClassDone.Homework", "log_homework failed, class=" + state.ClassCode, null, state.TeacherTelegramId);
                await bot.SendMessage(chatId, "❌ ذخیره تکلیف انجام نشد (مشکل سرور). فیدبک ثبت شده است.",
                    replyMarkup: RetryButton("fin"));
                return;
            }
            state.HomeworkSaved = true;
        }

        await Send(chatId,
            "✅ جلسه کامل ثبت شد\n\n" +
            "کلاس: " + state.ClassCode + "\n" +
            "درس: " + state.UnitLesson + "\n" +
            "حاضر: " + state.Students.Count(x => x.Status == "Present") + "\n" +
            "غایب: " + state.Students.Count(x => x.Status == "Absent") + "\n" +
            "مشارکت: " + state.Participation + "\n" +
            "فضای کلاس: " + state.Environment + "\n" +
            "نکته: " + (string.IsNullOrWhiteSpace(state.Note) ? "—" : state.Note) + "\n" +
            "تکلیف: " + (string.IsNullOrWhiteSpace(state.HomeworkText) ? "—" : state.HomeworkText) +
            (state.AssignedCount > 1 ? " (تعداد: " + state.AssignedCount + ")" : ""));

        // خلاصه جلسه در گروه کلاس (اگر گروه تنظیم شده باشد)
        await PostClassSummaryToGroup(chatId, state);

        // یادآوری فیدبک بعد از جلسه ۵ و ۱۰ (و اعلام فیدبک به مدرس برای زبان‌آموزان)
        await AfterSessionTermChecks(chatId, state);

        // به‌روزرسانی نتایج ترم همین کلاس (خطا در آن، classdone را fail نمی‌کند)
        RecomputeInBackground(state.ClassCode, "ClassDone.Recompute", force: true);

        state.Stage = "done";
        ClassDoneStates.TryRemove(state.TeacherTelegramId, out _);
    }

    // نسخه گروه: بدون مشارکت، فضای کلاس و نکته مدرس (آن‌ها فقط تو شیت و پیام خود مدرس می‌آیند)
    static async Task PostClassSummaryToGroup(long teacherChatId, ClassDoneState state)
    {
        long groupId = GetClassGroupId(state.ClassCode);
        if (groupId == 0)
        {
            AppLog.Warn("ClassDone.Group", "group chat id not set for class " + state.ClassCode, state.TeacherTelegramId);
            return;
        }

        int present = state.Students.Count(x => x.Status == "Present");
        int absent = state.Students.Count(x => x.Status == "Absent");
        int other = state.Students.Count - present - absent;

        var sb = new StringBuilder();
        sb.Append("🎓 ثبت جلسه انجام شد\n");

        string sessionLine = "";
        try
        {
            var prog = await PostToAppsScriptWithResult(new { action = "term_progress", class_code = state.ClassCode });
            if (IsOk(prog))
            {
                int n = (int)(JNum(prog!.Value, "sessions_held") ?? 0);
                if (n <= 0 && prog.Value.TryGetProperty("sessions", out var sess) && sess.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var it in sess.EnumerateArray())
                    {
                        string ul = it.ValueKind == JsonValueKind.String ? (it.GetString() ?? "").Trim() : "";
                        idx++;
                        if (ul.Length > 0 && ul == state.UnitLesson) { n = idx; break; }
                    }
                }
                if (n > 0) sessionLine = "جلسه: Session " + n + "\n";
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ClassDone.Group", "session number failed: " + ex.Message, state.TeacherTelegramId);
        }
        if (sessionLine.Length > 0) sb.Append(sessionLine);

        sb.Append("کلاس: " + state.ClassCode + "\n");
        sb.Append("مدرس: " + state.TeacherName + "\n");
        sb.Append("تاریخ: " + ToPersianDate(state.Date) + "\n");
        sb.Append("درس: " + state.UnitLesson + "\n");
        sb.Append("حاضر: " + present + "\n");
        sb.Append("غایب: " + absent + "\n");
        if (other > 0) sb.Append("تأخیر/سایر: " + other + "\n");
        sb.Append("تکلیف جلسه بعد: " + (string.IsNullOrWhiteSpace(state.HomeworkText) ? "—" : state.HomeworkText) + "\n");

        try
        {
            await SendLong(groupId, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            AppLog.Error("ClassDone.Group", "post to class group failed, class=" + state.ClassCode, ex, state.TeacherTelegramId);
            await Send(teacherChatId, "⚠️ جلسه ثبت شد، ولی ارسال خلاصه به گروه کلاس ناموفق بود.");
        }
    }

    static async Task<List<AttStudent>> LoadStudentsByClass(string classCode)
    {
        var list = new List<AttStudent>();
        try
        {
            var rows = await ReadCsv(StudentInfoCsvUrl, 30);
            if (rows.Count < 2) return list;
            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "student_id");
            int nameCol = FindCol(h, "full_name", "name", "student_name");
            int classCol = FindCol(h, "class_code");

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!Eq(Get(r, classCol), classCode)) continue;
                list.Add(new AttStudent
                {
                    StudentId = Get(r, idCol),
                    Name = Get(r, nameCol)
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadStudentsByClass", "exception", ex);
        }
        return list;
    }

    // زبان‌آموزهای شناسایی‌شده از پیام گروه کلاس را به لیست زبان‌آموزهای /classdone اضافه می‌کند
    static void MergeGroupStudents(List<AttStudent> students, string classCode)
    {
        foreach (var kv in GroupStudents)
        {
            var gs = kv.Value;
            if (!Eq(gs.ClassCode, classCode)) continue;
            if (students.Any(s => s.StudentId == gs.StudentId)) continue;
            students.Add(new AttStudent
            {
                StudentId = gs.StudentId,
                Name = string.IsNullOrWhiteSpace(gs.FullName) ? gs.StudentId : gs.FullName
            });
        }
    }

    // ===================== TEACHER MISTAKES (outside /classdone) =====================

    static async Task StartTeacherMistakes(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /mistakes را در چت خصوصی بزن.");
            return;
        }

        long teacherId = message.From!.Id;
        if (!Teachers.TryGetValue(teacherId, out var classes) || classes.Count == 0)
        {
            await Send(chatId, "شما به‌عنوان مدرس ثبت نشده‌اید.");
            return;
        }

        if (classes.Count == 1)
        {
            await BeginTeacherMistakes(teacherId, chatId, classes[0]);
            return;
        }

        TeacherMistakeStates[teacherId] = new TeacherMistakeState
        {
            TeacherId = teacherId,
            Stage = "class"
        };
        var buttons = classes.Select(c =>
            new[] { InlineKeyboardButton.WithCallbackData(c.ClassCode, "tm|class|" + c.ClassCode) }
        ).ToList();
        await bot.SendMessage(chatId, "اشتباهات کدام کلاس را ثبت می‌کنی؟",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    static async Task BeginTeacherMistakes(long teacherId, long chatId, TeacherClass tc)
    {
        var students = await LoadStudentsByClass(tc.ClassCode);
        MergeGroupStudents(students, tc.ClassCode);

        var state = new TeacherMistakeState
        {
            TeacherId = teacherId,
            ClassCode = tc.ClassCode,
            TeacherName = tc.TeacherName,
            Students = students,
            Stage = "student"
        };
        TeacherMistakeStates[teacherId] = state;

        if (students.Count == 0)
        {
            await Send(chatId, "برای کلاس " + tc.ClassCode + " زبان‌آموزی پیدا نشد.");
            return;
        }

        await AskTeacherMistakeStudent(chatId, state);
    }

    static async Task AskTeacherMistakeStudent(long chatId, TeacherMistakeState state)
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < state.Students.Count; i++)
        {
            var s = state.Students[i];
            string name = string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name;
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(name, "tm|stu|" + i) });
        }
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✅ پایان", "tm|done|0") });

        await bot.SendMessage(chatId,
            "کلاس: " + state.ClassCode + "\nزبان‌آموز را انتخاب کن:",
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    static async Task HandleTeacherMistakeCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long teacherId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;
        var parts = cq.Data.Split('|');
        if (parts.Length < 3) return;

        if (!Teachers.TryGetValue(teacherId, out var classes) || classes.Count == 0)
        {
            await bot.AnswerCallbackQuery(cq.Id, "دسترسی ندارید.");
            return;
        }

        if (parts[1] == "class")
        {
            var tc = classes.FirstOrDefault(x => x.ClassCode == parts[2]);
            if (tc == null) { await bot.AnswerCallbackQuery(cq.Id); return; }
            await bot.AnswerCallbackQuery(cq.Id);
            await BeginTeacherMistakes(teacherId, chatId, tc);
            return;
        }

        if (!TeacherMistakeStates.TryGetValue(teacherId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "منقضی شده. دوباره /mistakes بزن.");
            return;
        }

        if (parts[1] == "done")
        {
            TeacherMistakeStates.TryRemove(teacherId, out _);
            await bot.AnswerCallbackQuery(cq.Id);
            await Send(chatId, "ثبت اشتباهات تمام شد.");
            return;
        }

        if (parts[1] == "stu")
        {
            if (!int.TryParse(parts[2], out int idx) || idx < 0 || idx >= state.Students.Count)
            {
                await bot.AnswerCallbackQuery(cq.Id);
                return;
            }
            var s = state.Students[idx];
            state.StudentId = s.StudentId;
            state.Stage = "text";
            state.WaitingText = true;
            await bot.AnswerCallbackQuery(cq.Id);
            string name = string.IsNullOrWhiteSpace(s.Name) ? s.StudentId : s.Name;
            await Send(chatId,
                "اشتباهات «" + name + "» را بنویس. هر خط یک اشتباه:\n\n" +
                "جمله اشتباه => جمله درست\n\n" +
                "اختیاری، خط اول: unit_lesson=1_2 یا question_id=شناسه\n" +
                "(برای برگشت: /skip)");
            return;
        }

        await bot.AnswerCallbackQuery(cq.Id);
    }

    static async Task HandleTeacherMistakeText(Message message, TeacherMistakeState state, string text)
    {
        long chatId = message.Chat.Id;

        if (IsCommand(text, "/skip"))
        {
            state.WaitingText = false;
            state.Stage = "student";
            await AskTeacherMistakeStudent(chatId, state);
            return;
        }

        string unit = "", lesson = "", qid = "";
        var lines = text.Replace("\r", "").Split('\n').ToList();
        if (lines.Count > 0)
        {
            string first = lines[0].Trim();
            if (Regex.IsMatch(first, @"^(unit|lesson|unit_lesson|question_id|qid)\s*[=:]", RegexOptions.IgnoreCase))
            {
                var mUl = Regex.Match(first, @"unit_lesson\s*[=:]\s*(\d+)_(\d+)", RegexOptions.IgnoreCase);
                if (mUl.Success)
                {
                    unit = mUl.Groups[1].Value;
                    lesson = mUl.Groups[2].Value;
                }
                else
                {
                    var mu = Regex.Match(first, @"\bunit\s*[=:]\s*(\d+)", RegexOptions.IgnoreCase);
                    var ml = Regex.Match(first, @"\blesson\s*[=:]\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mu.Success) unit = mu.Groups[1].Value;
                    if (ml.Success) lesson = ml.Groups[1].Value;
                }
                var mq = Regex.Match(first, @"(?:question_id|qid)\s*[=:]\s*(\S+)", RegexOptions.IgnoreCase);
                if (mq.Success) qid = mq.Groups[1].Value;
                lines.RemoveAt(0);
            }
        }

        var items = ParseMistakeLines(string.Join("\n", lines));
        if (items.Count == 0)
        {
            await Send(chatId,
                "فرمت درست نبود. هر خط این‌طوری باشه:\nجمله اشتباه => جمله درست\n\n" +
                "(یا /skip برای برگشت)");
            return;
        }

        if (string.IsNullOrWhiteSpace(unit) && string.IsNullOrWhiteSpace(lesson))
        {
            string ul = await GetClassUnitLesson(state.ClassCode, "");
            if (!string.IsNullOrWhiteSpace(ul))
            {
                ParseUL(ul, out int u, out int l);
                unit = u.ToString();
                lesson = l.ToString();
            }
        }

        var st = state.Students.FirstOrDefault(s => s.StudentId == state.StudentId);
        string name = st == null || string.IsNullOrWhiteSpace(st.Name) ? state.StudentId : st.Name;

        var (ok, saveError) = await PostToAppsScriptWithError(new
        {
            action = "log_teacher_mistakes",
            date = TodayStr(),
            class_code = state.ClassCode,
            unit = unit,
            lesson = lesson,
            teacher_name = state.TeacherName,
            items = items.Select(x => new
            {
                student_id = state.StudentId,
                student_name = name,
                wrong = x.Wrong,
                correct = x.Correct,
                question_id = qid
            }).ToList()
        });

        if (!ok)
        {
            AppLog.Error("TeacherMistakes", "log_teacher_mistakes failed: " + saveError, null, state.TeacherId);
            string shortErr = saveError.Length > 200 ? saveError.Substring(0, 200) : saveError;
            await Send(chatId, "❌ ذخیره نشد.\nدلیل: " + shortErr + "\n\nدوباره بفرست یا /skip بزن.");
            return;
        }

        state.WaitingText = false;
        state.Stage = "student";
        await Send(chatId, "✅ " + items.Count + " اشتباه برای «" + name + "» ثبت شد.");
        await AskTeacherMistakeStudent(chatId, state);
    }

    // ===================== PRACTICE =====================

    static async Task StartPractice(Message message)
    {
        long chatId = message.Chat.Id;
        if (message.Chat.Type != ChatType.Private)
        {
            await Send(chatId, "لطفاً /practice را در چت خصوصی بزن.");
            return;
        }

        long userId = message.From!.Id;
        if (await BlockedByFeedbackGate(chatId, userId)) return;
        string telegramId = userId.ToString();
        var student = await FindStudentFull(telegramId);
        string studentId = student?.StudentId ?? telegramId;

        // سطح/پیشرفت معلوم را در Student_Info تثبیت کن (اگر Level خالی مانده بود)
        if (student != null && !string.IsNullOrWhiteSpace(student.ClassCode))
            MirrorStudentLevel(studentId, student.ClassCode,
                string.IsNullOrWhiteSpace(student.Level) ? GetClassLevel(student.ClassCode) : student.Level,
                student.CurrentUnit, student.CurrentLesson);

        string studentUL = NormalizeUnitLesson(student?.CurrentUnit, student?.CurrentLesson, "1_1");
        string classUL = student != null
            ? await GetClassUnitLesson(student.ClassCode, studentUL)
            : studentUL;

        // قفل جلسه: اگر جلسه‌ای در حال اجراست، درس/صف همان جلسه عوض نمی‌شود
        if (PracticeStates.TryGetValue(userId, out var active) && active.Index < active.Questions.Count)
        {
            await Send(chatId, "جلسه تمرین فعلی هنوز تمام نشده؛ همان درس/صف ادامه پیدا می‌کند. (برای شروع دوباره /cancel)");
            await SendCurrentQuestion(chatId, userId);
            return;
        }

        string practiceUL = studentUL;
        bool updateProgress = true;
        bool aheadOfClass = false;
        int cmp = CompareUnitLesson(studentUL, classUL);
        if (cmp > 0)
        {
            practiceUL = classUL;
            updateProgress = false; // جلوتر از کلاس: پیشرفت عوض نمی‌شه (پسرفت نداریم)
            aheadOfClass = true;
        }
        else if (cmp < 0)
        {
            // درس جدید کلاس اولویت دارد: اول بسته کامل درس جدید، بعد مرور/عقب‌افتاده‌ها
            practiceUL = classUL;
            updateProgress = true;
        }

        // اگر امروز برای همین درس جلسه کامل انجام شده، /practice بعدی همان روز جلسه روزانه است
        string fullKey = studentId + "|" + practiceUL;
        bool fullDone = FullPracticeDone.ContainsKey(fullKey);

        var story = await LoadStoryByUnitLesson(practiceUL);
        var lessonQuestions = story != null
            ? (await LoadPracticeQuestions(story.StoryId))
                .Where(q => !IsDailyPromptType(q.QuestionType))
                .OrderBy(q => q.Order).ToList()
            : new List<PracticeQuestion>();

        // ----- جلسه کامل (FULL): اولین /practice بعد از اینکه درس جاری شد -----
        if (!fullDone && lessonQuestions.Count > 0)
        {
            if (aheadOfClass)
                await Send(chatId,
                    "تو از درس کلاس جلوتری.\nامروز همان درس کلاس را تمرین می‌کنی: " + classUL +
                    "\n(موقعیت تو تغییر نمی‌کند.)");

            PracticeStates[userId] = new PracticeState
            {
                StudentId = studentId,
                StoryId = story!.StoryId,
                StudentName = student?.FullName ?? "",
                ClassCode = student?.ClassCode ?? "",
                UnitLesson = practiceUL,
                ClassUnitLesson = classUL,
                IsFull = true,
                IsReview = false,
                IsDaily = false,
                UpdateProgress = updateProgress,
                Questions = lessonQuestions,
                ShownStories = new HashSet<string>()
            };

            string intro = "🎯 تمرین کامل درس\n";
            if (student != null && !string.IsNullOrWhiteSpace(student.FullName))
                intro += "زبان‌آموز: " + student.FullName + "\n";
            intro += "موقعیت شما: " + studentUL + "\n";
            intro += "درس کلاس: " + classUL + "\n";
            intro += "تمرین امروز: " + practiceUL + "\n";
            intro += "عنوان: " + story.Title;
            await Send(chatId, intro);

            if (!string.IsNullOrWhiteSpace(story.FullStory))
                await SendLong(chatId, "📖 " + story.Title + "\n\n" + story.FullStory);

            await SendCurrentQuestion(chatId, userId);
            return;
        }

        // ----- جلسه روزانه (DAILY): بین کلاس‌ها یا بعد از انجام کامل -----
        var daily = await BuildDailySession(studentId, practiceUL, lessonQuestions);
        if (daily.Count == 0)
        {
            await Send(chatId, "برای درس " + practiceUL + " سوالی پیدا نشد.");
            return;
        }

        PracticeStates[userId] = new PracticeState
        {
            StudentId = studentId,
            StoryId = story?.StoryId ?? "",
                StudentName = student?.FullName ?? "",
                ClassCode = student?.ClassCode ?? "",
            UnitLesson = practiceUL,
            ClassUnitLesson = classUL,
            IsFull = false,
            IsReview = false,
            IsDaily = true,
            UpdateProgress = false,
            Questions = daily,
            ShownStories = new HashSet<string>()
        };

        await Send(chatId, "🎯 تمرین روزانه\n" + daily.Count + " سوال");
        await SendCurrentQuestion(chatId, userId);
    }

    // ساخت جلسه روزانه: اشتباهات سررسیده + سوالات درس فعلی (سبک) + مرور درس‌های گذشته
    static async Task<List<PracticeQuestion>> BuildDailySession(string studentId, string practiceUL,
        List<PracticeQuestion> lessonQuestions)
    {
        var result = new List<PracticeQuestion>();
        var seen = new HashSet<string>();

        void Add(PracticeQuestion q, bool due)
        {
            if (q == null) return;
            if (IsDailyPromptType(q.QuestionType)) return;
            if (!string.IsNullOrWhiteSpace(q.QuestionId) && !seen.Add(q.QuestionId)) return;
            q.IsDueReview = due;
            result.Add(q);
        }

        // a) اشتباهات سررسیده (اول)، کم‌دیده‌ترین‌ها اول
        foreach (var q in OrderByCoverage(studentId, await LoadDueReviewQuestions(studentId)))
        {
            if (result.Count >= DailyMistakeCap) break;
            Add(q, true);
        }

        // b) سوالات درس فعلی، کم‌دیده‌ترین‌ها اول (داستان دوباره پرسیده نمی‌شود)
        foreach (var q in OrderByCoverage(studentId, lessonQuestions))
        {
            if (result.Count >= DailyMaxQuestions) break;
            if (IsStoryHeavyType(q.QuestionType)) continue;
            Add(q, false);
        }

        // c) مرور سبک درس‌های گذشته: فقط انواع مجاز مرور، کم‌دیده‌ترین‌ها اول
        string prev = GetPreviousUnitLesson(practiceUL);
        int lessonsUsed = 0;
        while (lessonsUsed < 4 && !string.IsNullOrWhiteSpace(prev) && result.Count < DailyMaxQuestions)
        {
            var pStory = await LoadStoryByUnitLesson(prev);
            if (pStory != null)
            {
                var pQs = (await LoadPracticeQuestions(pStory.StoryId))
                    .Where(q => IsPreviousReviewType(q.QuestionType))
                    .ToList();
                int added = 0;
                foreach (var q in OrderByCoverage(studentId, pQs))
                {
                    if (added >= DailyReviewPerLesson || result.Count >= DailyMaxQuestions) break;
                    if (!string.IsNullOrWhiteSpace(q.QuestionId) && seen.Contains(q.QuestionId)) continue;
                    Add(q, false);
                    added++;
                }
                lessonsUsed++;
            }
            prev = GetPreviousUnitLesson(prev);
        }

        return result;
    }

    // انواع داستان‌محور که در مرور سبک روزانه نباید بیایند
    static bool IsStoryHeavyType(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        return t == "generalunderstanding" || t == "gist" || t == "detailedquestion" ||
               t == "detailed" || t == "storycomplete" || t.Contains("reading");
    }

    static string NormType(string? t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        var sb = new StringBuilder();
        foreach (char c in t)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // DailyPrompt is the canonical bank type; DailyPromt/DailyGrammar are legacy aliases.
    static bool IsDailyPromptType(string? t)
    {
        string n = NormType(t);
        return n == "dailyprompt" || n == "dailypromt" || n == "dailygrammar";
    }

    // انواع مجاز مرور درس‌های گذشته (داستان‌محور و DailyGrammar حذف می‌شوند)
    static bool IsPreviousReviewType(string? t)
    {
        switch (NormType(t))
        {
            case "cloze":
            case "fixmistake":
            case "choosesentence":
            case "unscramble":
            case "meaningtoenglishmcq":
            case "meaningtoenglishwrite":
            case "vocabcollocation":
            case "definition":
                return true;
            default:
                return false;
        }
    }

    // پیام «تمرین انجام شد» در گروه کلاس (تمرین کامل هر بار، تمرین روزانه یک بار در روز)
    static async Task PostPracticeDoneToGroup(PracticeState state, int total)
    {
        try
        {
            if (state.IsReview) return;
            if (string.IsNullOrWhiteSpace(state.ClassCode)) return;
            long groupId = GetClassGroupId(state.ClassCode);
            if (groupId == 0) return;

            if (state.IsDaily)
            {
                string key = state.StudentId + "|" + TodayStr();
                if (!PracticeDoneNotified.TryAdd(key, true)) return;
            }

            string name = string.IsNullOrWhiteSpace(state.StudentName) ? state.StudentId : state.StudentName;
            string kind = state.IsFull ? "تمرین کامل درس " + state.UnitLesson : "تمرین روزانه";
            string text = "✅ تمرین انجام شد\n" +
                "زبان‌آموز: " + name + "\n" +
                "کلاس: " + state.ClassCode + "\n" +
                "نوع: " + kind + "\n" +
                "نمره: " + state.CorrectCount + " از " + total;

            await bot.SendMessage(groupId, text);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Practice.Group", "post practice done failed: " + ex.Message);
        }
    }

    static async Task SendCurrentQuestion(long chatId, long userId)
    {
        if (!PracticeStates.TryGetValue(userId, out var state)) return;

        // Hard guard: a daily prompt must never be shown/answered inside practice.
        while (state.Index < state.Questions.Count &&
               IsDailyPromptType(state.Questions[state.Index].QuestionType))
        {
            var skipped = state.Questions[state.Index];
            AppLog.Warn("Practice", "skipped daily prompt inside practice: " +
                skipped.QuestionId + " type=" + skipped.QuestionType, userId);
            state.Index++;
        }

        if (state.Index >= state.Questions.Count)
        {
            int total = state.Questions.Count(q => !IsDailyPromptType(q.QuestionType));
            int correct = Math.Min(state.CorrectCount, total);
            int percent = total == 0 ? 0 : correct * 100 / total;

            // جلسه کامل امروز انجام شد (برای اینکه /practice بعدیِ همان روز دوباره بسته کامل ندهد).
            // اگر زبان‌آموز مردود شود (درصد کم)، علامت نمی‌زنیم تا بتواند همان روز کامل را دوباره بزند.
            if (state.IsFull && (percent >= PassPercent || !state.UpdateProgress))
                FullPracticeDone[state.StudentId + "|" + state.UnitLesson] = TodayStr();

            // ثبت اینکه امروز تمرین انجام شد (برای یادآوری)
            PostInBackground(new
            {
                action = "log_practice",
                student_id = state.StudentId,
                student_name = GroupStudentName(state.StudentId),
                unit_lesson = state.IsReview ? "review" : state.UnitLesson,
                correct = state.CorrectCount,
                total = total,
                is_review = state.IsReview
            });

            _ = Task.Run(() => PostPracticeDoneToGroup(state, total)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
            }, TaskContinuationOptions.OnlyOnFaulted);

            if (!state.IsReview)
            {
                if (state.IsDaily || !state.UpdateProgress)
                {
                    await Send(chatId,
                        "✅ تمرین درس " + state.UnitLesson + " تمام شد\n" +
                        "نمره: " + state.CorrectCount + " از " + total + "\n" +
                        "موقعیت تو تغییری نکرد.\n\nبرای ادامه: /practice");
                }
                else if (percent < PassPercent)
                {
                    await Send(chatId,
                        "تمرین درس " + state.UnitLesson + " تمام شد\n" +
                        "نمره: " + state.CorrectCount + " از " + total + "\n" +
                        "برای رفتن به درس بعد حداقل " + PassPercent + "٪ لازمه. همین درس را دوباره تمرین کن: /practice");
                }
                else
                {
                    string next = GetNextUnitLesson(state.UnitLesson);
                    ParseUL(next, out int u, out int l);

                    bool ok = await PostToAppsScript(new
                    {
                        action = "update_progress",
                        student_id = state.StudentId,
                        unit = u.ToString(),
                        lesson = l.ToString()
                    });

                    if (ok)
                    {
                        ProgressOverrides[state.StudentId] = next;
                        await Send(chatId,
                            "✅ تمرین درس " + state.UnitLesson + " تمام شد\n" +
                            "نمره: " + state.CorrectCount + " از " + total + "\n" +
                            "موقعیت جدید: " + next + "\n\nبرای ادامه: /practice");
                    }
                    else
                    {
                        AppLog.Error("Practice.Progress", "update_progress failed", null, userId);
                        await Send(chatId,
                            "✅ تمرین درس " + state.UnitLesson + " تمام شد\n" +
                            "نمره: " + state.CorrectCount + " از " + total + "\n" +
                            "⚠️ ذخیره موقعیت جدید انجام نشد. بعداً دوباره /practice بزن.");
                    }
                }
            }
            else
            {
                await Send(chatId,
                    "✅ مرور تمام شد\nنمره: " + state.CorrectCount + " از " + total +
                    "\n\nبرای ادامه: /practice");
            }

            PracticeStates.TryRemove(userId, out _);
            return;
        }

        var q = state.Questions[state.Index];
        QuestionLastAsked[state.StudentId + "|" + q.QuestionId] = DateTime.UtcNow;

        // سوال تایپی: اشتباه مدرس یا MeaningToEnglish_Write (خودارزیابی). بدون دکمه، منتظر متن زبان‌آموز
        if (q.Typed || IsSelfAssessWrite(q))
        {
            state.AwaitingTyped = true;
            state.AwaitingSelfMark = false;
            string head = state.IsReview ? "مرور " : "سوال ";
            string ask = IsSelfAssessWrite(q) ? "جمله را انگلیسی بنویس." : "جمله درست را تایپ کن.";
            await Send(chatId,
                head + (state.Index + 1) + " از " + state.Questions.Count + "\n" +
                "[" + q.QuestionType + "]\n\n" + q.Question +
                "\n\n" + ask + "\n(نمی‌دانی؟ /skip)");
            return;
        }
        state.AwaitingTyped = false;

        if (state.IsReview || q.IsDueReview)
        {
            string t = (q.QuestionType ?? "").ToLowerInvariant();
            bool needsStory = t.Contains("general") || t.Contains("detailed") || t.Contains("reading");
            if (needsStory && !string.IsNullOrWhiteSpace(q.StoryId) && !state.ShownStories.Contains(q.StoryId))
            {
                var story = await LoadStory(q.StoryId);
                if (story != null && !string.IsNullOrWhiteSpace(story.FullStory))
                    await SendLong(chatId, "📖 برای جواب:\n\n" + story.Title + "\n\n" + story.FullStory);
                state.ShownStories.Add(q.StoryId);
            }
        }

        var rows = new List<InlineKeyboardButton[]>();
        bool isStory = IsStoryComplete(q);
        int shown = isStory ? Math.Min(2, q.Options.Count) : q.Options.Count;   // StoryComplete: حداکثر ۲ گزینه
        for (int i = 0; i < shown; i++)
        {
            if (string.IsNullOrWhiteSpace(q.Options[i])) continue;
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(q.Options[i], "pq|" + state.Index + "|" + i) });
        }

        // جمله‌های بلند روی دکمه بریده می‌شن؛ پس متن کامل گزینه‌ها را هم تو پیام می‌آوریم
        string extra = "";
        if (isStory && q.Options.Take(shown).Any(o => o.Length > 35))
            extra = "\n\n" + string.Join("\n", q.Options.Take(shown).Select((o, k) => (k + 1) + ") " + o));

        string prefix = state.IsReview ? "مرور" : "سوال";
        await bot.SendMessage(chatId,
            prefix + " " + (state.Index + 1) + " از " + state.Questions.Count + "\n[" + q.QuestionType + "]\n\n" + q.Question + extra,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    // ----- StoryComplete -----

    static bool IsStoryComplete(PracticeQuestion q)
    {
        return string.Equals((q.QuestionType ?? "").Trim(), "StoryComplete", StringComparison.OrdinalIgnoreCase);
    }

    // MeaningToEnglish_Write: خودارزیابی (بدون مقایسه سختگیرانه رشته)
    static bool IsSelfAssessWrite(PracticeQuestion q)
    {
        string t = (q.QuestionType ?? "").Trim();
        return t.Equals("MeaningToEnglish_Write", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("MeaningToEnglishWrite", StringComparison.OrdinalIgnoreCase);
    }

    // اولین سوال StoryComplete در همین زنجیره (سوال‌های StoryComplete پشت سر هم)
    static int StoryChainStart(PracticeState state, int index)
    {
        int start = index;
        while (start > 0 && IsStoryComplete(state.Questions[start - 1])) start--;
        return start;
    }

    static async Task HandleStoryCompleteAnswer(CallbackQuery cq, PracticeState state, PracticeQuestion q,
        int optIndex, long chatId, long userId)
    {
        // مقایسه متنی با CorrectAnswer (Trim + بدون حساسیت به حروف بزرگ/کوچک)
        string chosen = (optIndex >= 0 && optIndex < q.Options.Count) ? q.Options[optIndex].Trim() : "";
        string correctText = (q.CorrectAnswer ?? "").Trim();
        bool correct = chosen.Length > 0 && string.Equals(chosen, correctText, StringComparison.OrdinalIgnoreCase);

        if (correct)
        {
            state.CorrectCount++;
            state.StoryCorrectInChain++;
            await bot.AnswerCallbackQuery(cq.Id, "✅");
            await Send(chatId, "✅ درست بود");

            state.Index++;
            // زنجیره با موفقیت و بدون خطا تمام شد: شمارنده برای زنجیره بعدی صفر می‌شود.
            if (state.Index >= state.Questions.Count)
                state.StoryCorrectInChain = 0;

            await SendCurrentQuestion(chatId, userId);
            return;
        }

        // غلط: بدون mark_correct (فقط جلسه‌های مرور mark_correct دارند).
        // نمره قدم‌های درست این زنجیره کم می‌شود تا بعد از شروع دوباره، دو بار حساب نشود.
        state.CorrectCount -= state.StoryCorrectInChain;
        state.StoryCorrectInChain = 0;

        await bot.AnswerCallbackQuery(cq.Id, "❌");
        await Send(chatId, "❌ غلط\nجمله درست:\n" + q.CorrectAnswer + "\n\nداستان از اول شروع می‌شود 🔁");

        PostInBackground(new
        {
            action = "log_mistake",
            student_id = state.StudentId,
            student_name = GroupStudentName(state.StudentId),
            question_id = q.QuestionId,
            skill = q.QuestionType
        });

        // فقط زنجیره StoryComplete از اول (سوال‌های قبلی مثل Detailed دوباره پرسیده نمی‌شن)
        state.Index = StoryChainStart(state, state.Index);
        await SendCurrentQuestion(chatId, userId);
    }
    // اگر correct_answer در شیت حرف (A/B/C/D) باشد، به متن گزینه تبدیل می‌شود
    static string ResolveCorrect(PracticeQuestion q)
    {
        string c = (q.CorrectAnswer ?? "").Trim();
        if (q.Options.Any(o => string.Equals(o.Trim(), c, StringComparison.OrdinalIgnoreCase)))
            return c;
        if (c.Length == 1)
        {
            int i = char.ToUpperInvariant(c[0]) - 'A';
            if (i >= 0 && i < q.Options.Count) return q.Options[i].Trim();
        }
        return c;
    }

    // ----- سوال تایپی (اشتباهات ثبت‌شده توسط مدرس) -----

    // فقط حروف و عددها می‌مونن. این‌ها نادیده گرفته می‌شن:
    // حروف بزرگ/کوچک، همه فاصله‌ها، نقطه‌گذاری، آپوستروف و گیومه (صاف یا منحنی)،
    // ایموجی و هر علامت دیگه، و شکل‌های خاص یونیکد.
    static string NormalizeAnswer(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static async Task FinishTypedQuestion(long chatId, long userId, PracticeState state,
        PracticeQuestion tq, bool correct, string resultText)
    {
        if (state.IsReview)
            RecentReview[state.StudentId + "|" + tq.QuestionId] = DateTime.UtcNow;

        await Send(chatId, resultText);

        if (correct)
        {
            state.CorrectCount++;
            if (state.IsReview || tq.IsDueReview)
                PostInBackground(new { action = "mark_correct", student_id = state.StudentId, question_id = tq.QuestionId });
        }
        else
        {
            PostInBackground(new
            {
                action = "log_mistake",
                student_id = state.StudentId,
                student_name = GroupStudentName(state.StudentId),
                question_id = tq.QuestionId,
                skill = tq.QuestionType
            });
        }

        state.Index++;
        await SendCurrentQuestion(chatId, userId);
    }

    static async Task HandleTypedAnswer(Message message, PracticeState state, string text)
    {
        long chatId = message.Chat.Id;
        long userId = message.From!.Id;
        bool skip = IsCommand(text, "/skip");

        await state.Gate.WaitAsync();
        try
        {
            if (state.Index >= state.Questions.Count) return;

            var tq = state.Questions[state.Index];
            if (!state.AwaitingTyped && !IsDailyPromptType(tq.QuestionType)) return;
            string typed = NormalizeAnswer(text);

            // فقط علامت یا ایموجی فرستاده: دوباره بخواه
            if (!skip && typed.Length == 0)
            {
                await Send(chatId, "لطفاً جمله را با حروف بنویس یا /skip بزن.");
                return;
            }

            state.AwaitingTyped = false;

            // Defensive fallback: if a legacy daily prompt is already the active question,
            // save the answer, confirm, and advance instead of leaving practice stuck.
            if (IsDailyPromptType(tq.QuestionType))
            {
                var (saved, saveErr) = await PostToAppsScriptWithError(new
                {
                    action = "log_daily_grammar_answer",
                    student_id = state.StudentId,
                    class_code = "",
                    question_id = tq.QuestionId,
                    date = TodayStr(),
                    answer = skip ? "" : text.Trim(),
                    status = "submitted"
                });
                state.AwaitingSelfMark = false;
                if (saved)
                    await Send(chatId, "✅ جواب ثبت شد.");
                else
                    AppLog.Warn("Practice", "daily prompt answer save failed: " + saveErr, userId);
                state.Index++;
                await SendCurrentQuestion(chatId, userId);
                return;
            }

            // MeaningToEnglish_Write: خودارزیابی؛ هیچ مقایسه سختگیرانه‌ای با جواب مدل نمی‌کنیم
            if (!skip && IsSelfAssessWrite(tq))
            {
                state.AwaitingSelfMark = true;
                var sm = new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ درست بود", "ps|" + state.Index + "|y"),
                        InlineKeyboardButton.WithCallbackData("❌ غلط بود", "ps|" + state.Index + "|n"),
                    }
                };
                string model = string.IsNullOrWhiteSpace(tq.CorrectAnswer) ? "—" : tq.CorrectAnswer;
                await bot.SendMessage(chatId,
                    "جواب مدل:\n" + model + "\n\nآیا جوابت درست بود؟",
                    replyMarkup: new InlineKeyboardMarkup(sm));
                return;
            }

            // درست
            if (!skip && typed == NormalizeAnswer(tq.CorrectAnswer))
            {
                await FinishTypedQuestion(chatId, userId, state, tq, true, "✅ درست بود");
                return;
            }

            // رد کرد، یا همان جمله اشتباه را دوباره نوشت
            if (skip || typed == NormalizeAnswer(tq.WrongText))
            {
                await FinishTypedQuestion(chatId, userId, state, tq, false,
                    "❌ " + (skip ? "رد شد" : "این هنوز همان جمله اشتباه است") +
                    "\nجواب درست:\n" + tq.CorrectAnswer);
                return;
            }

            // با جواب معلم فرق داره؛ شاید جواب درست دیگه‌ای باشه
            state.AwaitingSelfMark = true;
            var buttons = new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ جوابم هم درست بود", "ps|" + state.Index + "|y"),
                    InlineKeyboardButton.WithCallbackData("❌ غلط بود", "ps|" + state.Index + "|n"),
                }
            };
            await bot.SendMessage(chatId,
                "جواب معلم:\n" + tq.CorrectAnswer + "\n\nجواب تو با این فرق دارد. آیا جواب تو هم درست بود؟",
                replyMarkup: new InlineKeyboardMarkup(buttons));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task HandleSelfMarkCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;

        if (!PracticeStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /practice بزن.");
            return;
        }

        var parts = cq.Data.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int qIndex))
        {
            await bot.AnswerCallbackQuery(cq.Id);
            return;
        }
        bool yes = parts[2] == "y";

        await state.Gate.WaitAsync();
        try
        {
            if (!state.AwaitingSelfMark || qIndex != state.Index || qIndex >= state.Questions.Count)
            {
                await bot.AnswerCallbackQuery(cq.Id, "این دکمه قدیمیه.");
                return;
            }

            state.AwaitingSelfMark = false;
            await bot.AnswerCallbackQuery(cq.Id);
            var tq = state.Questions[qIndex];
            await FinishTypedQuestion(chatId, userId, state, tq, yes,
                yes ? "✅ ثبت شد: درست" : "❌ ثبت شد: غلط");
        }
        finally
        {
            state.Gate.Release();
        }
    }

    static async Task HandlePracticeCallback(CallbackQuery cq)
    {
        if (cq.Data == null || cq.Message == null) return;
        long userId = cq.From.Id;
        long chatId = cq.Message.Chat.Id;

        if (!PracticeStates.TryGetValue(userId, out var state))
        {
            await bot.AnswerCallbackQuery(cq.Id, "جلسه منقضی شده. دوباره /practice بزن.");
            return;
        }

        var parts = cq.Data.Split('|');
        if (parts.Length != 3 ||
            !int.TryParse(parts[1], out int qIndex) ||
            !int.TryParse(parts[2], out int optIndex))
        {
            await bot.AnswerCallbackQuery(cq.Id);
            return;
        }

        await state.Gate.WaitAsync();
        try
        {
            if (qIndex != state.Index || qIndex >= state.Questions.Count)
            {
                await bot.AnswerCallbackQuery(cq.Id, "این سوال قبلاً جواب داده شده.");
                return;
            }

            var q = state.Questions[qIndex];

            // StoryComplete: ساخت داستان قدم‌به‌قدم. هیچ ثبتی تو Student_Mistakes نداره
            if (IsStoryComplete(q))
            {
                await HandleStoryCompleteAnswer(cq, state, q, optIndex, chatId, userId);
                return;
            }
            string chosen = (optIndex >= 0 && optIndex < q.Options.Count) ? q.Options[optIndex].Trim() : "";
            string correctText = ResolveCorrect(q);
            bool correct = string.Equals(chosen, correctText, StringComparison.OrdinalIgnoreCase);

            if (state.IsReview || q.IsDueReview)
                RecentReview[state.StudentId + "|" + q.QuestionId] = DateTime.UtcNow;

            if (correct)
            {
                state.CorrectCount++;
                await bot.AnswerCallbackQuery(cq.Id, "✅");
                await Send(chatId, "✅ درست بود");
                if (state.IsReview || q.IsDueReview)
                    PostInBackground(new { action = "mark_correct", student_id = state.StudentId, question_id = q.QuestionId });
            }
            else
            {
                await bot.AnswerCallbackQuery(cq.Id, "❌");
                await Send(chatId, "❌ غلط\nجواب درست: " + correctText);
                PostInBackground(new
                {
                    action = "log_mistake",
                    student_id = state.StudentId,
                    student_name = GroupStudentName(state.StudentId),
                    question_id = q.QuestionId,
                    skill = q.QuestionType
                });
            }

            state.Index++;
            await SendCurrentQuestion(chatId, userId);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // ===================== APPS SCRIPT =====================

    static bool IsOk(JsonElement? r)
    {
        return r.HasValue &&
               r.Value.ValueKind == JsonValueKind.Object &&
               r.Value.TryGetProperty("ok", out var p) &&
               p.ValueKind == JsonValueKind.True;
    }

    static string JStr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        if (!e.TryGetProperty(name, out var p)) return "";
        if (p.ValueKind == JsonValueKind.Null) return "";
        if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
        return p.ToString();
    }

    // ثبت بی‌صدا در پس‌زمینه (زبان‌آموز منتظر نمی‌مونه). تا ۲ بار تلاش می‌کنه.
    static void PostInBackground(object payload)
    {
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (await PostToAppsScript(payload)) return;
                await Task.Delay(1500);
            }
            AppLog.Error("PostInBackground", "failed after retries");
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppLog.Error("BackgroundTask", "[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // true فقط وقتی Apps Script جواب {"ok":true} بدهد
    static async Task<bool> PostToAppsScript(object payload)
    {
        var r = await PostToAppsScriptWithResult(payload);
        return IsOk(r);
    }

    static async Task<JsonElement?> PostToAppsScriptWithResult(object payload)
    {
        var (result, _) = await PostToAppsScriptDetailed(payload);
        return result;
    }

    // مثل PostToAppsScript ولی متن خطا را هم برمی‌گرداند (برای نمایش به مدرس)
    static async Task<(bool Ok, string Error)> PostToAppsScriptWithError(object payload)
    {
        var (result, error) = await PostToAppsScriptDetailed(payload);
        if (IsOk(result)) return (true, "");
        if (result.HasValue)
        {
            string serverError = JStr(result.Value, "error");
            return (false, string.IsNullOrWhiteSpace(serverError) ? "پاسخ سرور ok نبود" : serverError);
        }
        return (false, error);
    }

    static async Task<(JsonElement? Result, string Error)> PostToAppsScriptDetailed(object payload, bool logErrors = true)
    {
        string error = "";
        string actionName = "?";
        try
        {
            var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
            actionName = node["action"]?.ToString() ?? "?";
            if (!string.IsNullOrEmpty(BotSecret)) node["secret"] = BotSecret;
            string json = node.ToJsonString();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var content = new StringContent(json, Encoding.UTF8, "text/plain");
            var response = await appsClient.PostAsync(AppsScriptUrl, content);

            for (int i = 0; i < 5 && (int)response.StatusCode >= 300 && (int)response.StatusCode < 400; i++)
            {
                var location = response.Headers.Location;
                if (location == null) break;
                var target = location.IsAbsoluteUri ? location : new Uri(new Uri(AppsScriptUrl), location);
                response = await appsClient.GetAsync(target);
            }

            string body = await response.Content.ReadAsStringAsync();
            Console.WriteLine("AppsScript (" + sw.ElapsedMilliseconds + " ms): " + (body.Length > 400 ? body.Substring(0, 400) : body));

            if (body.TrimStart().StartsWith("{"))
            {
                var root = JsonSerializer.Deserialize<JsonElement>(body);

                // ok=false: هشدار (خطاهای مهم‌تر را خود جاهایی که نتیجه را چک می‌کنن ERROR می‌کنن)
                if (logErrors && !IsOk(root))
                {
                    string serverError = JStr(root, "error");
                    if (!serverError.Contains("No homework"))
                        AppLog.Warn("PostToAppsScript", "action=" + actionName + " ok=false: " + serverError);
                }
                return (root, "");
            }

            error = "جواب سرور JSON نبود (کد " + (int)response.StatusCode + "). احتمالاً Deploy نسخه جدید یا Authorization";
            if (logErrors)
                AppLog.Error("PostToAppsScript",
                    "action=" + actionName + " non-JSON response, status=" + (int)response.StatusCode + " body=" + Snippet(body));
        }
        catch (Exception ex)
        {
            error = "خطای ارتباط: " + ex.Message;
            if (logErrors)
                AppLog.Error("PostToAppsScript", "action=" + actionName + " exception", ex);
        }
        return (null, error);
    }

    // ===================== DATA =====================

    static async Task<StudentRow?> FindStudentFull(string studentId)
    {
        try
        {
            var rows = await ReadCsv(StudentInfoCsvUrl, 30);
            if (rows.Count < 2) return null;

            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "student_id");
            int nameCol = FindCol(h, "full_name", "name", "student_name");
            int classCol = FindCol(h, "class_code");
            int levelCol = FindCol(h, "level");
            int unitCol = FindCol(h, "current_unit");
            int lessonCol = FindCol(h, "current_lesson");

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (Get(r, idCol) != studentId) continue;

                var s = new StudentRow
                {
                    StudentId = studentId,
                    FullName = Get(r, nameCol),
                    ClassCode = Get(r, classCol),
                    Level = Get(r, levelCol),
                    CurrentUnit = Get(r, unitCol),
                    CurrentLesson = Get(r, lessonCol)
                };

                // اگر شیت هنوز آپدیت نشده، پیشرفتی که تازه ثبت کردیم را اعمال کن
                if (ProgressOverrides.TryGetValue(studentId, out var ov))
                {
                    string sheetUL = NormalizeUnitLesson(s.CurrentUnit, s.CurrentLesson, "1_1");
                    if (CompareUnitLesson(ov, sheetUL) > 0)
                    {
                        ParseUL(ov, out int ou, out int ol);
                        s.CurrentUnit = ou.ToString();
                        s.CurrentLesson = ol.ToString();
                    }
                    else
                    {
                        ProgressOverrides.TryRemove(studentId, out _);
                    }
                }
                return s;
            }
        }
        catch (Exception ex) { AppLog.Error("FindStudentFull", "exception", ex); }
        return null;
    }

    static async Task<string> GetClassUnitLesson(string classCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(classCode)) return fallback;
        try
        {
            var term = await GetTermStructure(classCode);
            bool termActive = term != null && term.Active && term.Sessions.Count > 0;
            DateTime termStart = term != null ? (ParseDate(term.StartDate) ?? DateTime.MinValue) : DateTime.MinValue;

            var rows = await ReadCsv(AttendanceCsvUrl, 30);
            if (rows.Count < 2) return termActive ? term!.Sessions[0] : fallback;

            var h = NormalizeHeaders(rows[0]);
            int classCol = FindCol(h, "class_code");
            int unitCol = FindCol(h, "unit");
            int lessonCol = FindCol(h, "lesson");
            int heldCol = FindCol(h, "held");
            int dateCol = FindCol(h, "date");

            string best = "";
            DateTime bestDate = DateTime.MinValue;

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!Eq(Get(r, classCol), classCode)) continue;
                if (heldCol >= 0 && !IsHeldValue(Get(r, heldCol))) continue;

                string ul = NormalizeUnitLesson(Get(r, unitCol), Get(r, lessonCol), "");
                if (string.IsNullOrWhiteSpace(ul)) continue;
                DateTime date = ParseDate(Get(r, dateCol)) ?? DateTime.MinValue;
                if (termActive)
                {
                    if (termStart != DateTime.MinValue && date != DateTime.MinValue && date < termStart) continue;
                    if (!term!.Sessions.Any(x => string.Equals(x, ul, StringComparison.OrdinalIgnoreCase))) continue;
                }

                if (string.IsNullOrWhiteSpace(best) ||
                    date > bestDate ||
                    (date == bestDate && CompareUnitLesson(ul, best) > 0))
                {
                    best = ul;
                    bestDate = date;
                }
            }

            if (!string.IsNullOrWhiteSpace(best)) return best;

            if (termActive) return term!.Sessions[0];
            return fallback;
        }
        catch (Exception ex)
        {
            AppLog.Error("GetClassUnitLesson", "exception", ex);
            return fallback;
        }
    }

    static bool RecentlyReviewed(string studentId, string qid)
    {
        return RecentReview.TryGetValue(studentId + "|" + qid, out var t) &&
               (DateTime.UtcNow - t).TotalHours < 12;
    }

    static DateTime QuestionLastAskedAt(string studentId, string qid)
    {
        if (string.IsNullOrWhiteSpace(qid)) return DateTime.MinValue;
        return QuestionLastAsked.TryGetValue(studentId + "|" + qid, out var t) ? t : DateTime.MinValue;
    }

    // کم‌دیده‌ترین/دیده‌نشده‌ها اول؛ سپس ترتیب بانک
    static List<PracticeQuestion> OrderByCoverage(string studentId, IEnumerable<PracticeQuestion> questions)
    {
        return questions
            .OrderBy(q => QuestionLastAskedAt(studentId, q.QuestionId))
            .ThenBy(_ => Random.Shared.Next())
            .ToList();
    }

    static async Task<List<PracticeQuestion>> LoadDueReviewQuestions(string studentId)
    {
        var result = new List<PracticeQuestion>();
        try
        {
            var rows = await ReadCsv(MistakesCsvUrl, 30);
            if (rows.Count < 2) return result;

            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "student_id");
            int qCol = FindCol(h, "question_id");
            int nextCol = FindCol(h, "next_review");
            int masteredCol = FindCol(h, "mastered");
            DateTime today = NowIran().Date;
            var dueIds = new List<string>();

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (Get(r, idCol) != studentId) continue;
                string mastered = Get(r, masteredCol).ToLowerInvariant();
                if (mastered == "yes" || mastered == "true" || mastered == "1") continue;

                DateTime? next = ParseDate(Get(r, nextCol));
                if (next == null || next.Value <= today)
                {
                    string qid = Get(r, qCol);
                    if (!string.IsNullOrWhiteSpace(qid) && !RecentlyReviewed(studentId, qid))
                        dueIds.Add(qid);
                }
            }

            var all = await LoadAllPracticeQuestions();
            var map = new Dictionary<string, PracticeQuestion>();
            foreach (var q in all)
                if (!string.IsNullOrWhiteSpace(q.QuestionId) && !map.ContainsKey(q.QuestionId))
                    map[q.QuestionId] = q;

            var teacherMap = await LoadTeacherMistakeQuestions(
                dueIds.Distinct().Where(x => x.StartsWith("T-")).ToList());

            foreach (var qid in dueIds.Distinct())
            {
                if (map.TryGetValue(qid, out var q)) result.Add(q);
                else if (teacherMap.TryGetValue(qid, out var tq2)) result.Add(tq2);
                if (result.Count >= MaxReviewPerSession) break;
            }
        }
        catch (Exception ex) { AppLog.Error("LoadDueReviewQuestions", "exception", ex); }
        return result;
    }

    // اشتباهات ثبت‌شده توسط مدرس (شناسه‌هایی که با T- شروع می‌شن) را از Apps Script می‌گیرد
    static async Task<Dictionary<string, PracticeQuestion>> LoadTeacherMistakeQuestions(List<string> ids)
    {
        var map = new Dictionary<string, PracticeQuestion>();
        if (ids.Count == 0) return map;

        try
        {
            var res = await PostToAppsScriptWithResult(new { action = "get_teacher_mistakes", ids = ids });
            if (!IsOk(res) || !res!.Value.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var it in arr.EnumerateArray())
            {
                string id = JStr(it, "id");
                string wrong = JStr(it, "wrong");
                string correct = JStr(it, "correct");
                if (id == "" || wrong == "" || correct == "") continue;

                map[id] = new PracticeQuestion
                {
                    QuestionId = id,
                    QuestionType = "teacher-correction",
                    Question = "✍️ این جمله اشتباه دارد. آن را درست بنویس:\n\n" + wrong,
                    CorrectAnswer = correct,
                    WrongText = wrong,
                    Typed = true
                };
            }
        }
        catch (Exception ex) { AppLog.Error("LoadTeacherMistakeQuestions", "exception", ex); }
        return map;
    }

    static async Task<List<PracticeQuestion>> LoadAllPracticeQuestions()
    {
        var list = new List<PracticeQuestion>();
        try
        {
            var rows = await ReadCsv(PracticeQuestionsCsvUrl, 300);
            if (rows.Count < 2) return list;
            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "question_id");
            int storyCol = FindCol(h, "story_id");
            int typeCol = FindCol(h, "question_type");
            int qCol = FindCol(h, "question", "question_text");
            int aCol = FindCol(h, "option_a");
            int bCol = FindCol(h, "option_b");
            int cCol = FindCol(h, "option_c");
            int dCol = FindCol(h, "option_d");
            int correctCol = FindCol(h, "correct_answer", "correct");
            int orderCol = FindCol(h, "order_in_lesson", "order");
            int activeCol = FindCol(h, "active");

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (IsInactive(Get(r, activeCol))) continue;
                var options = new List<string>();
                foreach (var col in new[] { aCol, bCol, cCol, dCol })
                {
                    string v = Get(r, col);
                    if (!string.IsNullOrWhiteSpace(v)) options.Add(v);
                }
                int order = 0;
                int.TryParse(Get(r, orderCol), out order);
                list.Add(new PracticeQuestion
                {
                    QuestionId = Get(r, idCol),
                    StoryId = Get(r, storyCol),
                    QuestionType = Get(r, typeCol),
                    Question = Get(r, qCol),
                    Options = options,
                    CorrectAnswer = Get(r, correctCol),
                    Order = order
                });
            }
        }
        catch (Exception ex) { AppLog.Error("LoadAllPracticeQuestions", "exception", ex); }
        return list;
    }

    static async Task<StoryInfo?> LoadStory(string storyId)
    {
        try
        {
            var rows = await ReadCsv(StoriesCsvUrl, 300);
            if (rows.Count < 2) return null;
            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "story_id");
            int titleCol = FindCol(h, "title");
            int storyCol = FindCol(h, "full_story");
            int unitCol = FindCol(h, "unit");
            int lessonCol = FindCol(h, "lesson");
            int activeCol = FindCol(h, "active");
            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (Get(r, idCol) != storyId) continue;
                if (IsInactive(Get(r, activeCol))) continue;
                return new StoryInfo
                {
                    StoryId = storyId,
                    Title = Get(r, titleCol),
                    FullStory = Get(r, storyCol),
                    Unit = Get(r, unitCol),
                    Lesson = Get(r, lessonCol)
                };
            }
        }
        catch (Exception ex) { AppLog.Error("LoadStory", "exception", ex); }
        return null;
    }

    static async Task<StoryInfo?> LoadStoryByUnitLesson(string unitLesson)
    {
        try
        {
            var rows = await ReadCsv(StoriesCsvUrl, 300);
            if (rows.Count < 2) return null;
            var h = NormalizeHeaders(rows[0]);
            int idCol = FindCol(h, "story_id");
            int unitCol = FindCol(h, "unit");
            int lessonCol = FindCol(h, "lesson");
            int titleCol = FindCol(h, "title");
            int storyCol = FindCol(h, "full_story");
            int activeCol = FindCol(h, "active");

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (IsInactive(Get(r, activeCol))) continue;
                string unit = Get(r, unitCol);
                string lesson = Get(r, lessonCol);
                string ul = NormalizeUnitLesson(unit, lesson, unit);
                if (ul == unitLesson || unit == unitLesson)
                {
                    return new StoryInfo
                    {
                        StoryId = Get(r, idCol),
                        Title = Get(r, titleCol),
                        FullStory = Get(r, storyCol),
                        Unit = ul
                    };
                }
            }
        }
        catch (Exception ex) { AppLog.Error("LoadStoryByUnitLesson", "exception", ex); }
        return null;
    }

    static async Task<List<PracticeQuestion>> LoadPracticeQuestions(string storyId)
    {
        return (await LoadAllPracticeQuestions()).Where(q => q.StoryId == storyId).OrderBy(q => q.Order).ToList();
    }

    // ===================== UNIT / LESSON HELPERS =====================

    static string NormalizeUnitLesson(string? unit, string? lesson, string fallback)
    {
        unit = (unit ?? "").Trim();
        lesson = (lesson ?? "").Trim();
        if (unit.Contains("_")) return unit;
        if (!string.IsNullOrWhiteSpace(unit) && !string.IsNullOrWhiteSpace(lesson))
            return unit + "_" + lesson;
        if (!string.IsNullOrWhiteSpace(unit)) return unit;
        return fallback;
    }

    static int CompareUnitLesson(string a, string b)
    {
        ParseUL(a, out int au, out int al);
        ParseUL(b, out int bu, out int bl);
        if (au != bu) return au.CompareTo(bu);
        return al.CompareTo(bl);
    }

    static void ParseUL(string ul, out int unit, out int lesson)
    {
        unit = 1; lesson = 1;
        var p = (ul ?? "").Split('_');
        if (p.Length > 0) int.TryParse(p[0], out unit);
        if (p.Length > 1) int.TryParse(p[1], out lesson);
        if (unit <= 0) unit = 1;
        if (lesson <= 0) lesson = 1;
    }

    static string GetNextUnitLesson(string current)
    {
        ParseUL(current, out int u, out int l);
        l++;
        if (l > LessonsPerUnit) { l = 1; u++; }
        return u + "_" + l;
    }

    static string GetPreviousUnitLesson(string current)
    {
        ParseUL(current, out int u, out int l);
        l--;
        if (l < 1) { l = LessonsPerUnit; u--; }
        if (u < 1) return "";
        return u + "_" + l;
    }

    static bool IsHeldValue(string value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return v == "yes" || v == "true" || v == "1" || v == "held" || v == "y";
    }

    static bool Eq(string a, string b)
    {
        return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ===================== CSV =====================

    // ttlSeconds: چند ثانیه نتیجه کش بماند. اگر دانلود شکست بخورد و کش قدیمی باشد، از همان استفاده می‌شود.
    static async Task<List<List<string>>> ReadCsv(string url, int ttlSeconds)
    {
        if (CsvCache.TryGetValue(url, out var cached) &&
            (DateTime.UtcNow - cached.At).TotalSeconds < ttlSeconds)
            return cached.Rows;

        try
        {
            string fetchUrl = url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            string text = await http.GetStringAsync(fetchUrl);
            var rows = ParseCsv(text);
            CsvCache[url] = new CsvCacheItem { At = DateTime.UtcNow, Rows = rows };
            return rows;
        }
        catch (Exception ex)
        {
            if (cached != null)
            {
                AppLog.Warn("ReadCsv", "fetch failed (gid=" + CsvGid(url) + "), using stale cache: " + ex.Message);
                return cached.Rows;
            }
            throw;
        }
    }

    static List<List<string>> ParseCsv(string text)
    {
        text = text.TrimStart('\uFEFF');
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '"' && inQuotes && n == '"') { field.Append('"'); i++; }
            else if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if ((c == '\n' || c == '\r') && !inQuotes)
            {
                if (field.Length > 0 || row.Count > 0)
                {
                    row.Add(field.ToString().Trim());
                    rows.Add(row);
                    row = new List<string>();
                    field.Clear();
                }
                if (c == '\r' && n == '\n') i++;
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }
        return rows;
    }

    static List<string> NormalizeHeaders(List<string> headers) =>
        headers.Select(x => (x ?? "").Trim().Trim('\uFEFF').ToLowerInvariant().Replace(" ", "_")).ToList();

    // اول تطابق دقیق با هر نام، بعد (فقط اگر دقیقاً یک ستون شامل نام اول باشد) تطابق جزئی
    static int FindCol(List<string> headers, params string[] names)
    {
        foreach (var n0 in names)
        {
            string n = n0.ToLowerInvariant();
            for (int i = 0; i < headers.Count; i++)
                if (headers[i] == n) return i;
        }

        string first = names[0].ToLowerInvariant();
        int found = -1, count = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].Contains(first)) { found = i; count++; }
        }
        return count == 1 ? found : -1;
    }

    static string Get(List<string> row, int idx) =>
        (idx < 0 || idx >= row.Count) ? "" : (row[idx] ?? "");

    static bool IsInactive(string value)
    {
        string a = (value ?? "").Trim().ToLowerInvariant();
        return a == "no" || a == "false" || a == "0";
    }

    // ===================== LOG (AppLog) =====================
    // استفاده:
    //   AppLog.Info("Place", "پیام");
    //   AppLog.Warn("Place", "پیام", userId);
    //   AppLog.Error("Place", "پیام", ex, userId);   // ← ERROR: کنسول + فایل logs/تاریخ.log + شیت System_Logs + پیام به AdminIds
    // خود AppLog هیچ‌وقت exception نمی‌ده و باعث کرش نمی‌شه.
    static class AppLog
    {
        private static readonly object FileLock = new object();
        private static readonly string Dir = string.IsNullOrWhiteSpace(LogDirOverride)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "logs")
            : LogDirOverride;
        private static readonly Regex TokenRegex = new Regex(@"(bot)?\d{6,}:[A-Za-z0-9_\-]{30,}", RegexOptions.Compiled);

        // جلوگیری از سیل پیام: یک ERROR تکراری در ۱۰ دقیقه فقط یک بار به شیت/ادمین می‌ره
        private static readonly ConcurrentDictionary<string, DateTime> Recent = new();
        private static readonly object RateLock = new object();
        private static DateTime RateStart = DateTime.UtcNow;
        private static int RateCount = 0;
        private const int RateMaxPer5Min = 10;

        public static string LogDir => Dir;

        public static void Info(string place, string message, long? userId = null)
            => Write("INFO", place, message, null, userId);

        public static void Warn(string place, string message, long? userId = null)
            => Write("WARN", place, message, null, userId);

        public static void Error(string place, string message, Exception? ex = null, long? userId = null)
            => Write("ERROR", place, message, ex, userId);

        private static void Write(string level, string place, string message, Exception? ex, long? userId)
        {
            try
            {
                string ts = NowIran().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string msg = Clean(message, 500);
                string exMsg = ex != null ? Clean(ex.GetType().Name + ": " + ex.Message, 300) : "";
                string stack = ex != null ? Clean(ex.StackTrace ?? "", 800) : "";

                string line = ts + " | " + level + " | " + place + " | user=" + (userId?.ToString() ?? "-") + " | " + msg;
                string consoleLine = line + (exMsg != "" ? " | ex=" + exMsg : "");
                string fileLine = consoleLine + (stack != "" ? " | stack=" + stack : "");

                Console.WriteLine(consoleLine);
                WriteFile(ts, fileLine);

                if (level == "ERROR") SendRemote(place, userId, msg, exMsg);
            }
            catch (Exception logEx) { Console.Error.WriteLine("[WARN] " + nameof(Write) + ": " + logEx.Message); }
        }

        // توکن و secret هرگز تو لاگ نمی‌آن؛ خط‌های جدید هم حذف می‌شن
        private static string Clean(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " / ");
            if (BotToken != "TOKEN_HERE" && BotToken.Length > 8) s = s.Replace(BotToken, "***");
            if (!string.IsNullOrEmpty(BotSecret)) s = s.Replace(BotSecret, "***");
            s = TokenRegex.Replace(s, "***");
            if (s.Length > max) s = s.Substring(0, max) + "…";
            return s;
        }

        private static bool fileErrorShown = false;

        private static void WriteFile(string ts, string line)
        {
            try
            {
                lock (FileLock)
                {
                    System.IO.Directory.CreateDirectory(Dir);
                    string file = System.IO.Path.Combine(Dir, ts.Substring(0, 10) + ".log");
                    System.IO.File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // فقط یک بار تو کنسول می‌گیم (لاگ‌کردنِ این خطا نباید حلقه بسازه)
                if (!fileErrorShown)
                {
                    fileErrorShown = true;
                    Console.WriteLine("LOG FILE ERROR: " + ex.Message + " | dir=" + Dir);
                }
            }
        }

        private static void SendRemote(string place, long? userId, string msg, string exMsg)
        {
            try
            {
                var now = DateTime.UtcNow;

                string key = place + "|" + (msg.Length > 80 ? msg.Substring(0, 80) : msg);
                if (Recent.Count > 300)
                    foreach (var kv in Recent)
                        if ((now - kv.Value).TotalMinutes > 10) Recent.TryRemove(kv.Key, out _);

                if (Recent.TryGetValue(key, out var last) && (now - last).TotalMinutes < 10) return;
                Recent[key] = now;

                lock (RateLock)
                {
                    if ((now - RateStart).TotalMinutes >= 5) { RateStart = now; RateCount = 0; }
                    if (RateCount >= RateMaxPer5Min) return;
                    RateCount++;
                }

                string text =
                    "🚨 ERROR\n" +
                    "Place: " + place + "\n" +
                    "User: " + (userId?.ToString() ?? "-") + "\n" +
                    "Msg: " + Trunc(msg, 300) +
                    (exMsg != "" ? "\nEx: " + Trunc(exMsg, 200) : "");

                string sheetMessage = msg + (exMsg != "" ? " | ex=" + exMsg : "");
                string uid = userId?.ToString() ?? "";

                _ = Task.Run(async () =>
                {
                    // ۱) شیت System_Logs (بدون لاگ‌کردن خطای خودش، تا حلقه نشه)
                    try
                    {
                        await PostToAppsScriptDetailed(new
                        {
                            action = "log_system",
                            level = "ERROR",
                            source = "Bot",
                            place = place,
                            user_id = uid,
                            message = sheetMessage
                        }, false);
                    }
                    catch (Exception ex) { Console.Error.WriteLine("[WARN] " + nameof(SendRemote) + ": " + ex.Message); }

                    // ۲) پیام تلگرام به ادمین‌ها
                    foreach (var id in AdminIds.Distinct())
                    {
                        try { if (bot != null) await bot.SendMessage(id, text); }
                        catch (Exception ex) { Console.Error.WriteLine("[WARN] " + nameof(SendRemote) + ": " + ex.Message); }
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Console.Error.WriteLine("[BG ERROR] Task failed: " + t.Exception?.GetBaseException().Message);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { Console.Error.WriteLine("[WARN] " + nameof(SendRemote) + ": " + ex.Message); }
        }

        private static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;

        // فایل‌های لاگ قدیمی‌تر از چند روز پاک می‌شن
        public static void CleanOld(int keepDays = 30)
        {
            try
            {
                if (!System.IO.Directory.Exists(Dir)) return;
                foreach (var f in System.IO.Directory.GetFiles(Dir, "*.log"))
                    if ((DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(f)).TotalDays > keepDays)
                        System.IO.File.Delete(f);
            }
            catch (Exception ex) { Console.Error.WriteLine("[WARN] " + nameof(CleanOld) + ": " + ex.Message); }
        }
    }

    static long? ParseUserId(string? s)
    {
        return long.TryParse(s, out var v) ? v : (long?)null;
    }

    // از آدرس CSV فقط شماره gid را برای لاگ نگه می‌داریم (نه کل آدرس)
    static string CsvGid(string url)
    {
        var m = Regex.Match(url ?? "", @"gid=(\d+)");
        return m.Success ? m.Groups[1].Value : "?";
    }

    static string Snippet(string? s, int n = 150)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length > n ? s.Substring(0, n) : s;
    }

    // ===================== MODELS =====================

    class CsvCacheItem
    {
        public DateTime At;
        public List<List<string>> Rows = new();
    }

    class TeacherClass
    {
        public string ClassCode = "";
        public string TeacherName = "";
        public string Level = "";
    }

    // یک کلاس ثبت‌شده از روی گروه تلگرام (عنوان = کد کلاس، توضیحات = level/term/مدرس)
    class ClassRegistration
    {
        public string ClassCode = "";
        public long GroupChatId;
        public string Level = "";
        public string Term = "";
        public long TeacherId;
        public string TeacherName = "";
        public bool Active = true;
        public string Source = "";
        public DateTime UpdatedAt = DateTime.MinValue;

        public ClassRegistration Clone() => new ClassRegistration
        {
            ClassCode = ClassCode,
            GroupChatId = GroupChatId,
            Level = Level,
            Term = Term,
            TeacherId = TeacherId,
            TeacherName = TeacherName,
            Active = Active,
            Source = Source,
            UpdatedAt = UpdatedAt
        };
    }

    // نقش‌های یک گروه کلاس: owner=مدیر آکادمی، administrator=مدرس، بقیه=زبان‌آموز
    class GroupRoles
    {
        public HashSet<long> AdminIds = new();   // همه ادمین‌ها (owner + administrator) = staff
        public HashSet<long> OwnerIds = new();   // فقط مالک/سازنده گروه = مدیر آکادمی
        public HashSet<long> TeacherIds = new(); // administrator (بدون owner) = مدرس
        public string ClassCode = "";
        public DateTime At = DateTime.MinValue;      // زمان آخرین رفرش موفق
        public bool Loaded = false;                  // اگر false باشد، هیچ‌کس student ثبت نمی‌شود
        public bool Refreshing = false;
    }
    static string? GradeFromReaction(string emoji)
    {
        emoji = (emoji ?? "").Replace("\uFE0F", "").Trim();
        if (emoji == "🔥" || emoji == "🏆" || emoji == "💯" || emoji == "⭐" ||
            emoji == "🌟" || emoji == "🥇" || emoji == "👏" || emoji == "🙌" ||
            emoji == "❤" || emoji == "🎉" || emoji == "🥳" || emoji == "🤩" ||
            emoji == "😍" || emoji == "💥" || emoji == "👌")
            return "5";
        if (emoji == "👍") return "3";
        if (emoji == "✍️" || emoji == "✍") return "2";
        if (emoji == "👎") return "1";
        return null;
    }

    static async Task HandleHomeworkReaction(MessageReactionUpdated reaction)
    {
        try
        {
            long chatId = reaction.Chat.Id;
            int messageId = reaction.MessageId;
            var user = reaction.User;
            if (user == null || user.IsBot) return;

            AppLog.Info("HomeworkReaction", "seen chat=" + chatId + " msg=" + messageId +
                " user=" + user.Id + " new=" + (reaction.NewReaction?.Length ?? 0), user.Id);

            var roles = await GetGroupRoles(chatId, false);
            if (roles == null || !roles.Loaded)
                roles = await GetGroupRoles(chatId, true);
            if (roles == null || !roles.Loaded) return;

            bool isStaff = false;
            try
            {
                // اگر خطا گرفت، خط پایین را با اسم لیست ادمین خودت عوض کن
                isStaff = (roles.AdminIds != null && roles.AdminIds.Contains(user.Id)) ||
                          IsAcademyAdmin(user.Id);
            }
            catch { return; }
            if (!isStaff) return;

            string emoji = "";
            string? grade = null;
            if (reaction.NewReaction != null)
            {
                foreach (var r in reaction.NewReaction)
                {
                    if (r is ReactionTypeEmoji re && !string.IsNullOrEmpty(re.Emoji))
                    {
                        var mapped = GradeFromReaction(re.Emoji);
                        if (mapped != null) { emoji = re.Emoji; grade = mapped; break; }
                    }
                }
            }

            if (grade == null) return;

            var payload = new
            {
                action = "grade_homework",
                chat_id = chatId.ToString(),
                group_chat_id = chatId.ToString(),
                message_id = messageId.ToString(),
                grade = grade,
                grader_id = user.Id.ToString(),
                emoji = emoji
            };

            var (ok, err) = await PostToAppsScriptWithError(payload);
            if (ok) AppLog.Info("HomeworkGrade", "grade=" + grade + " msg=" + messageId, user.Id);
            else AppLog.Warn("HomeworkGrade", "grade failed: " + err + " msg=" + messageId, user.Id);
        }
        catch (Exception ex)
        {
            AppLog.Error("HomeworkGrade", "reaction failed", ex);
        }
    }
    // زبان‌آموز شناسایی‌شده از پیام گروه (نسخه حافظه‌ای ردیف Student_Info)
    class GroupStudent
    {
        public string StudentId = "";
        public string ClassCode = "";
        public long ChatId;
        public string FullName = "";
        public string Username = "";
        public string Level = "";
        public DateTime FirstSeen = DateTime.MinValue;
        public DateTime LastSeen = DateTime.MinValue;
        public int MessageCount = 0;
    }
    class GroupStaff
    {
        public long UserId;
        public string ClassCode = "";
        public string FullName = "";
        public string Username = "";
        public string Title = "";
        public string Role = "";   // admin | teacher
        public DateTime FirstSeen;
        public DateTime LastSeen;
    }

    class TeacherMistakeState
    {
        public long TeacherId;
        public string ClassCode = "";
        public string TeacherName = "";
        public List<AttStudent> Students = new();
        public string Stage = "";
        public string StudentId = "";
        public bool WaitingText = false;
    }

    class ClassDoneState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public long TeacherTelegramId;
        public string ClassCode = "";
        public string TeacherName = "";
        public string Level = "";
        public string Date = "";
        public string Unit = "";
        public string Lesson = "";
        public string UnitLesson = "";
        public List<AttStudent> Students = new();
        public int StudentIndex = 0;
        public string Participation = "";
        public string Environment = "";
        public string Note = "";
        public string HomeworkText = "";
        public int AssignedCount = 1;
        public bool WaitingText = false;
        public string TextMode = "";
        public string Stage = "";
        public bool FeedbackSaved = false;
        public bool HomeworkSaved = false;
        public Dictionary<int, int> MistakeCounts = new();
        public int MistakeStudentIndex = -1;
        // اطلاعات شروع ترم (قبل از ثبت در Class_Terms)
        public string TermSetupStep = "";      // "" | classtype | package | weekdays | startdate | daytime | endtermdate | level | units
        public string TermClassType = "";
        public string TermPackage = "";
        public string TermWeekdays = "";
        public string TermStartTime = "";
        public string TermEndTime = "";
        public string TermStartDate = "";
        public string TermEndDate = "";
        public Dictionary<string, string> TermDayTimes = new();   // weekday -> "HH:mm-HH:mm"
        public int TermDayIndex = 0;
        public string TermLevel = "";
        public string TermBook = "";
        public string TermUnit1 = "";
        public string TermUnit2 = "";
    }

    class TermStructure
    {
        public bool HasTerm = false;
        public bool Active = false;
        public string Term = "";
        public int Book = 0;
        public string Unit1 = "";
        public string Unit2 = "";
        public string Level = "";
        public string StartDate = "";
        public int SessionsHeld = 0;
        public int TotalSessions = 0;
        public List<string> Sessions = new();
    }

    class FbCriterion
    {
        public string Key = "";
        public string Title = "";
        public string Question = "";
        public string Note = "";
        public string[] Short = new string[5];   // متن کوتاه روی دکمه
        public string[] Full = new string[5];    // متن کامل کیفی (تو پیام و شیت)
    }

    class TeacherFbState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public long UserId;
        public string StudentTelegramId = "";   // فقط برای کنترل تکراری‌بودن سمت سرور (تو شیت گزارش ذخیره نمی‌شه)
        public string ClassCode = "";
        public string TeacherName = "";
        public long TeacherId = 0;
        public int Index = 0;
        public int[] Scores = new int[TeacherCriteria.Length];
        public string Step = "rate";            // rate, text, continue, satisfaction, save
        public string FreeText = "";
        public string ContinueNextTerm = "";
        public int SatisfactionPercent = -1;
        public bool WaitingText = false;
    }

    class StudentFbState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public long TeacherId;
        public string TeacherName = "";
        public string ClassCode = "";
        public string Term = "";
        public int Round = 1;
        public string Phase = "";               // Mid | Final
        public int Held = 0;
        public int Total = 0;
        public int Mid = 0;
        public bool HasTerm = false;
        public bool Early = false;              // دور فیدبک هنوز نرسیده
        public List<AttStudent> Students = new();
        public HashSet<string> DoneIds = new();
        public string StudentId = "";
        public string StudentName = "";
        public int Index = 0;
        public int[] Scores = new int[StudentCriteria.Length];
        public string Step = "student";         // student, rate, strengths, free, save
        public string Strengths = "";
        public string FreeText = "";
        public bool WaitingText = false;
    }

    class PlacementQuestion
    {
        public string Id = "";
        public int Book = 0;
        public int Unit = 0;
        public string Type = "";
        public string Question = "";
        public List<string> Options = new();
        public string Correct = "";
    }

    class PlacementState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public long UserId;
        public string Username = "";
        public string TelegramName = "";
        public string FullName = "";
        public string Phone = "";
        public string Step = "intro";          // intro, name, phone, test, done
        public DateTime StartedUtc = DateTime.UtcNow;
        public List<PlacementQuestion> Bank = new();
        public int Book = 1;
        public List<PlacementQuestion> Current = new();
        public List<bool> StageResults = new();
        public int Index = 0;
        public int Seq = 0;
        public Dictionary<int, string> BookScores = new();
        public int TotalAsked = 0;
        public int TotalCorrect = 0;
        public int ResultBook = 1;
        public int ResultUnit = 1;
        public bool PassedAll = false;
        public string PreviousStudy = "";      // "yes" | "no"
        public string LanguageBackground = ""; // اگر سابقه زبان دارد
        public string Scores = "";
    }

    class PlacementPreferenceState
    {
        public long UserId;
        public string TelegramId = "";
        public string AttemptId = "";
        public string Step = "days";            // days, time, intensity, course
        public string FreeDays = "";
        public string FreeTime = "";
        public string Intensity = "";
        public int DaysPerWeek = 0;
        public string CourseType = "";
        public bool WaitingText = false;
        public string FullName = "";
        public string Username = "";
        public string Phone = "";
        public int ResultBook = 0;
        public int ResultUnit = 0;
        public bool PassedAll = false;
        public string PreviousStudy = "";
        public string LanguageBackground = "";
        public string Scores = "";
    }

    class DailyGrammarState
    {
        public long UserId;
        public string StudentId = "";
        public string StudentName = "";
        public string ClassCode = "";
        public string Level = "";
        public string Unit = "";
        public string Lesson = "";
        public string QuestionId = "";
        public string Question = "";
        public List<string> Options = new();
        public string CorrectAnswer = "";
        public bool WaitingAnswer = false;
        public bool AwaitingSelfMark = false;
        public bool SelfMarkCorrect = false;
        public int MessageId = 0;
    }

    class TeacherScheduleState
    {
        public long TeacherId;
        public string TargetTeacherId = "";
        public string EditId = "";
        public string TeacherName = "";
        public string ClassCode = "";
        public string Weekday = "";
        public string StartTime = "";
        public string EndTime = "";
        public string Step = "weekday";         // weekday, dayaction, time
        public bool WaitingText = false;
        public bool Availability = false;
    }

    class MistakeItem
    {
        public string Wrong = "";
        public string Correct = "";
    }

    class AttStudent
    {
        public string StudentId = "";
        public string Name = "";
        public string Status = "";
    }

    class HomeworkState
    {
        public string StudentId = "";
        public string StudentName = "";
        public string ClassCode = "";
        public string Unit = "";
        public string Lesson = "";
        public string HomeworkText = "";
        public bool WaitingSubmission = false;
        public string Mode = "";              // "" | edit | next
        public int AssignedCount = 1;
        public int SubmittedCount = 0;
        public int PendingRow = -1;
        public bool LastGraded = false;
        public int EditRow = 0;
        public HashSet<int> EditableRows = new();
    }

    class PracticeState
    {
        public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public string StudentId = "";
        public string StudentName = "";
        public string ClassCode = "";
        public string StoryId = "";
        public string UnitLesson = "";
        public string ClassUnitLesson = "";
        public bool IsReview = false;
        public bool IsFull = false;           // جلسه کامل درس (بسته کامل) در برابر جلسه روزانه
        public bool IsDaily = false;          // جلسه روزانه (بدون پیشرفت درس)
        public bool UpdateProgress = true;
        public bool AwaitingTyped = false;
        public bool AwaitingSelfMark = false;
        public int StoryCorrectInChain = 0;   // قدم‌های درست زنجیره StoryComplete (برای شمارش درست نمره)
        public List<PracticeQuestion> Questions = new();
        public int Index = 0;
        public int CorrectCount = 0;
        public HashSet<string> ShownStories = new();
    }

    class PracticeQuestion
    {
        public string QuestionId = "";
        public string StoryId = "";
        public string QuestionType = "";
        public string Question = "";
        public List<string> Options = new();
        public string CorrectAnswer = "";
        public int Order = 0;
        public bool Typed = false;
        public string WrongText = "";
        public bool IsDueReview = false;      // از اشتباهات سررسیده (برای mark_correct در جلسه روزانه)
    }

    class StoryInfo
    {
        public string StoryId = "";
        public string Title = "";
        public string FullStory = "";
        public string Unit = "";
        public string Lesson = "";
    }

    class StudentRow
    {
        public string StudentId = "";
        public string FullName = "";
        public string ClassCode = "";
        public string Level = "";
        public string CurrentUnit = "";
        public string CurrentLesson = "";
    }
}
