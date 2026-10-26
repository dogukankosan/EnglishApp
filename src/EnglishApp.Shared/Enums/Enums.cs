namespace EnglishApp.Shared.Enums;

// DB'deki tinyint kolonlarla birebir aynı değerler (docs/02_DB_TASARIMI.md)

public enum UserRole : byte { Admin = 1, User = 2 }

public enum CefrLevel : byte { A1 = 1, A2 = 2, B1 = 3, B2 = 4, C1 = 5, C2 = 6 }

public enum ContentStatus : byte { Draft = 0, Published = 1, Archived = 2 }

public enum PartOfSpeech : byte
{
    Noun = 1, Verb = 2, Adjective = 3, Adverb = 4, Pronoun = 5, Preposition = 6,
    Conjunction = 7, Determiner = 8, Interjection = 9, Phrase = 10, PhrasalVerb = 11, Other = 12
}

public enum CardType : byte
{
    WordMeaning = 1,       // kelimeyi gör/dinle → anlamı hatırla
    WordListening = 2,     // sadece dinle → anlamı hatırla
    SentenceListening = 3, // cümleyi dinle → anla
    SentenceCloze = 4,     // boşluk doldur
    Production = 5         // TR → EN (ileride)
}

public enum CardState : byte { New = 0, Learning = 1, Review = 2, Relearning = 3 }

public enum Rating : byte { Again = 1, Hard = 2, Good = 3, Easy = 4 }

public enum KnownWordSource : byte { PlacementTest = 1, Manual = 2 }

public enum RevokeReason : byte { Rotated = 1, Logout = 2, PasswordChanged = 3, ReuseDetected = 4, AdminAction = 5 }

public enum MailType : byte { DailyReminder = 1, WeeklyReport = 2, Test = 3, AccountInfo = 4 }

public enum MailStatus : byte { Sent = 1, Failed = 2 }

public enum LlmProvider : byte { Gemini = 1, OpenRouter = 2, Groq = 3 }

public enum AiFeature : byte { RolePlay = 1, Writing = 2, WordExplain = 3, ContentGeneration = 4, Other = 5, Tts = 6, Stt = 7 }

public enum ConversationMode : byte { RolePlay = 1, FreeTalk = 2 }

public enum ChatRole : byte { System = 1, User = 2, Assistant = 3 }

public enum WordProgressFilter : byte { All = 0, Learning = 1, Learned = 2, Struggling = 3, Known = 4 }
