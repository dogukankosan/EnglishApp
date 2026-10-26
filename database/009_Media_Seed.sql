/* =============================================================
   EnglishApp - Dizi / YouTube / podcast önerileri (başlangıç listesi). Tekrar çalıştırmak güvenli.
   Linkler arama bağlantısıdır: kanal adresi değişse de her zaman çalışır.
   MediaType: 1 Dizi, 2 YouTube, 3 Podcast, 4 Film   Level: 1 A1 ... 6 C2
   ============================================================= */
SET NOCOUNT ON;
GO

MERGE content.MediaRecommendations AS t
USING (VALUES
 -- YouTube
 (2, N'Easy English (Easy Languages)', 1, 'mix', N'Sokakta gerçek insanlarla yapılan röportajlar, İngilizce ve altyazılı. Gerçek konuşma hızını ve farklı aksanları duymak için ideal.', N'Önce İngilizce altyazıyla izle, sonra altyazısız tekrar dinle.', N'https://www.youtube.com/results?search_query=Easy+English+street+interviews', 10),
 (2, N'BBC Learning English', 1, 'uk', N'Kısa dersler, günlük kalıplar ve "6 Minute English" bölümleri. İngiliz aksanı, net ve yavaş konuşma.', N'"English at Work" serisi iş hayatı kalıpları için çok iyi.', N'https://www.youtube.com/results?search_query=BBC+Learning+English', 20),
 (2, N'English with Lucy', 2, 'uk', N'Telaffuz, günlük ifadeler ve İngiliz kültürü üzerine anlaşılır dersler.', N'Telaffuz videolarını izlerken "Tekrar et" özelliğiyle birlikte çalış.', N'https://www.youtube.com/results?search_query=English+with+Lucy', 30),
 (2, N'Rachel''s English', 2, 'us', N'Amerikan İngilizcesinde birleşen sesleri (gonna, wanna, didja) en iyi anlatan kanal. Anlamak için birebir.', N'Dikte çalışmasıyla birlikte izlersen kulağın hızla açılır.', N'https://www.youtube.com/results?search_query=Rachel%27s+English+reductions', 40),
 (2, N'Learn English with TV Series', 3, 'mix', N'Dizi ve film sahnelerini cümle cümle açıklayan dersler. Gerçek, hızlı konuşmayı anlamaya geçiş için çok iyi.', N'Beğendiğin cümleleri "Cümle yakala" ile kartlarına ekle.', N'https://www.youtube.com/results?search_query=Learn+English+with+TV+Series', 50),
 (2, N'TED-Ed', 4, 'mix', N'5 dakikalık animasyonlu kısa dersler. Konular ilginç, konuşma net. B1-B2 için harika dinleme.', NULL, N'https://www.youtube.com/results?search_query=TED-Ed', 60),
 -- Podcast
 (3, N'6 Minute English (BBC)', 2, 'uk', N'Her bölüm 6 dakika: iki sunucu bir konuyu konuşur, yeni kelimeleri açıklar. Transkripti de var.', N'Yolda dinlemek için ideal; haftada 3 bölüm hedefle.', N'https://www.youtube.com/results?search_query=6+Minute+English+BBC', 70),
 (3, N'English Learning for Curious Minds', 3, 'mix', N'İlginç gerçek hikâyeler, yavaş ve net anlatım. Sıkılmadan uzun dinleme yapmak için.', NULL, N'https://www.youtube.com/results?search_query=English+Learning+for+Curious+Minds+podcast', 80),
 (3, N'Luke''s English Podcast', 4, 'uk', N'Doğal, sohbet tarzı uzun bölümler. B1 üstü için gerçek İngiliz konuşmasına alışma.', NULL, N'https://www.youtube.com/results?search_query=Luke%27s+English+Podcast', 90),
 -- Dizi
 (1, N'Extra English', 1, 'uk', N'Dil öğrenenler için çekilmiş komik bir sitcom. Yavaş, açık konuşma ve basit kelimeler. Dizi izlemeye ilk adım.', N'Önce Türkçe altyazı değil, İngilizce altyazıyla başla.', N'https://www.youtube.com/results?search_query=Extra+English+episode+1', 100),
 (1, N'Peppa Pig', 1, 'uk', N'Çocuk çizgi filmi ama A1-A2 için mükemmel: kısa bölümler, net İngiliz aksanı, günlük durumlar.', N'Utanma, birçok yetişkin öğrenci böyle başlıyor.', N'https://www.youtube.com/results?search_query=Peppa+Pig+full+episodes+English', 110),
 (1, N'Friends', 3, 'us', N'Günlük Amerikan konuşmasının klasiği. Kısa sahneler, tekrar eden kalıplar, bol "gonna/wanna".', N'Bildiğin bir bölümle başla; her bölümden 3 cümle yakala.', N'https://www.imdb.com/find/?q=Friends', 120),
 (1, N'Modern Family', 3, 'us', N'Aile içi günlük konuşmalar, net Amerikan İngilizcesi. Mockumentary tarzı röportaj kısımları anlamayı kolaylaştırır.', NULL, N'https://www.imdb.com/find/?q=Modern+Family', 130),
 (1, N'The Office (US)', 4, 'us', N'İş ortamı, toplantılar, ofis dili. Yazılımcı olarak işine yarayacak kalıplar bol.', NULL, N'https://www.imdb.com/find/?q=The+Office+US', 140),
 (1, N'Ted Lasso', 4, 'mix', N'Amerikan ve İngiliz aksanını aynı dizide duyarsın. Pozitif, sıcak ve günlük dil dolu.', N'Aksan farkına dikkat et: Ted (ABD) vs. takım (İngiltere).', N'https://www.imdb.com/find/?q=Ted+Lasso', 150),
 (1, N'Sherlock', 5, 'uk', N'Hızlı ve zengin İngiliz İngilizcesi. B2 ve üstü için meydan okuma.', NULL, N'https://www.imdb.com/find/?q=Sherlock', 160),
 -- Film
 (4, N'The Intern', 3, 'us', N'İş hayatı ve gündelik konuşmalar, sakin tempo. Anlaşılır diyaloglarla keyifli bir film.', NULL, N'https://www.imdb.com/find/?q=The+Intern', 170),
 (4, N'Paddington', 2, 'uk', N'Londra''da geçen sıcak bir aile filmi. Net İngiliz aksanı, seyahat ve şehir hayatı kalıpları.', N'Londra seyahat kitinle birlikte izle.', N'https://www.imdb.com/find/?q=Paddington', 180)
) AS s (MediaType, Title, Level, Accent, DescriptionTr, TipTr, Url, SortOrder)
ON t.Title = s.Title
WHEN MATCHED THEN UPDATE SET MediaType = s.MediaType, Level = s.Level, Accent = s.Accent, DescriptionTr = s.DescriptionTr, TipTr = s.TipTr, Url = s.Url, SortOrder = s.SortOrder
WHEN NOT MATCHED THEN INSERT (MediaType, Title, Level, Accent, DescriptionTr, TipTr, Url, SortOrder)
                      VALUES (s.MediaType, s.Title, s.Level, s.Accent, s.DescriptionTr, s.TipTr, s.Url, s.SortOrder);
GO
