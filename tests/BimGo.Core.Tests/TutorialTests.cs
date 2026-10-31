using System.Collections.Generic;
using System.Linq;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Tutorial round: the tutorials' steps and tips, a run through them, when they are offered, the settings they keep
    /// and the viewer copy's id.
    /// </summary>
    [TestClass]
    public sealed class TutorialTests
    {
        private static IEnumerable<(string Where, string Text)> AllTexts()
        {
            foreach (bool viewer in new[] { false, true })
            {
                foreach (TutorialChapter chapter in Tutorials.For(viewer))
                {
                    foreach (TutorialStep step in chapter.Steps) { yield return ($"{chapter.Id}/{step.Id}", step.Text); }
                }
            }
            foreach (TutorialTip tip in TutorialTips.ALL) { yield return ($"tip {tip.Id}", tip.Text); }
        }

        [TestMethod]
        public void Content_KeysAreActionsAndIdsAreUnique()
        {
            foreach ((string where, string text) in AllTexts())
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{where} has no text");
                foreach (string name in Tutorials.Placeholders(text))
                {
                    Assert.AreNotEqual(InputAction.None, InputCatalog.FromId(name), $"{where}: {{{name}}} isn't an action");
                }
            }
            foreach (bool viewer in new[] { false, true })
            {
                TutorialChapter[] chapters = Tutorials.For(viewer);
                Assert.AreEqual(chapters.Length, chapters.Select(c => c.Id).Distinct().Count(), "chapter ids repeat");
                foreach (TutorialChapter chapter in chapters)
                {
                    Assert.IsTrue(chapter.Steps.Length > 0, chapter.Id);
                    Assert.AreEqual(chapter.Steps.Length, chapter.Steps.Select(s => s.Id).Distinct().Count(), $"{chapter.Id}: step ids repeat");
                    Assert.IsTrue(chapter.Steps.All(s => s.Amount > 0f), chapter.Id);
                }
            }
            Assert.AreEqual(TutorialTips.ALL.Length, TutorialTips.ALL.Select(t => t.Id).Distinct().Count(), "tip ids repeat");
            Assert.IsNotNull(TutorialTips.Find("gun-measure"));
            Assert.IsNull(TutorialTips.Find("nope"));

            // The full tutorial is the long one, in chapters; the viewer's is one short chapter
            Assert.IsTrue(Tutorials.StepCount(Tutorials.FULL, _ => true) > 25);
            Assert.AreEqual(1, Tutorials.VIEWER.Length);
            Assert.IsTrue(Tutorials.StepCount(Tutorials.VIEWER, _ => true) <= 12);
            Assert.AreSame(Tutorials.FULL[1], Tutorials.Chapter(false, Tutorials.FULL[1].Id));
            Assert.IsNull(Tutorials.Chapter(true, "moving"));
        }

        [TestMethod]
        public void Format_FillsInKeysAndLeavesOtherBraces()
        {
            string text = Tutorials.Format("Press {Fly} to fly, {Nope} stays, {Jump}{Crouch} and { a", a => a == InputAction.Fly ? "V" : a == InputAction.Jump ? "Space" : null);
            Assert.AreEqual("Press V to fly, {Nope} stays, Space(no key) and { a", text);
            Assert.AreEqual(string.Empty, Tutorials.Format(null, _ => "x"));
            Assert.AreEqual("plain", Tutorials.Format("plain", null));
            CollectionAssert.AreEqual(new[] { "Fly", "Nope" }, Tutorials.Placeholders("{Fly} and {Nope} and {open").ToArray());
        }

        [TestMethod]
        public void Run_SkipsStepsWhoseNeedsArentMetAndReportsFinishedChapters()
        {
            // No levels, rooms or site photos: their steps are left out
            var run = new TutorialRun(Tutorials.FULL, _ => false);
            int all = Tutorials.FULL.Sum(c => c.Steps.Length);
            int needy = Tutorials.FULL.Sum(c => c.Steps.Count(s => s.Need != TutorialNeed.None));
            Assert.IsTrue(needy > 0);
            Assert.AreEqual(all - needy, run.Count);
            Assert.AreEqual(run.Count, Tutorials.StepCount(Tutorials.FULL, _ => false));
            Assert.AreEqual(all, new TutorialRun(Tutorials.FULL, _ => true).Count);

            Assert.IsTrue(run.IsFirst);
            Assert.AreEqual("moving", run.Chapter.Id);
            run.Back(); // nothing before the first
            Assert.AreEqual(0, run.Index);

            int moving = Tutorials.FULL[0].Steps.Count(s => s.Need == TutorialNeed.None);
            for (int i = 0; i < moving - 1; i++) { Assert.IsFalse(run.Next()); }
            Assert.AreEqual(0, run.FinishedChapters.Count);
            Assert.IsFalse(run.Next()); // past the chapter's last step
            CollectionAssert.AreEqual(new[] { "moving" }, run.FinishedChapters.ToList());
            Assert.AreEqual("guns", run.Chapter.Id);
            run.Back();
            Assert.AreEqual("moving", run.Chapter.Id);

            while (!run.Next()) { }
            Assert.IsTrue(run.Finished);
            Assert.IsNull(run.Step);
            CollectionAssert.AreEqual(Tutorials.FULL.Select(c => c.Id).ToList(), run.FinishedChapters.ToList());
            Assert.IsTrue(run.Next()); // stays finished
            run.Back();
            Assert.IsTrue(run.IsLast);

            // One chapter on its own
            var one = new TutorialRun(new[] { Tutorials.VIEWER[0] }, n => n == TutorialNeed.SitePhotos);
            Assert.AreEqual(Tutorials.VIEWER[0].Steps.Length, one.Count);
            Assert.IsFalse(new TutorialRun(new[] { Tutorials.VIEWER[0] }, null).Finished);
        }

        [TestMethod]
        public void Offers_FullOncePerPcAndViewerOncePerCopy()
        {
            var settings = new LaunchSettings();
            Assert.IsTrue(TutorialOffers.ShouldOffer(settings, viewer: false, null));
            TutorialOffers.MarkOffered(settings, viewer: false, null);
            Assert.IsFalse(TutorialOffers.ShouldOffer(settings, viewer: false, null));
            Assert.IsTrue(settings.WelcomeShown);

            // Seen the full one: each viewer copy still offers its own, once
            Assert.IsTrue(TutorialOffers.ShouldOffer(settings, viewer: true, "copy-a"));
            TutorialOffers.MarkOffered(settings, viewer: true, "copy-a");
            Assert.IsFalse(TutorialOffers.ShouldOffer(settings, viewer: true, " copy-a "));
            Assert.IsTrue(TutorialOffers.ShouldOffer(settings, viewer: true, "copy-b"));

            // Older viewer files (no id) share one flag, set by any viewer offer
            Assert.IsFalse(TutorialOffers.ShouldOffer(settings, viewer: true, null));
            var fresh = new LaunchSettings();
            Assert.IsTrue(TutorialOffers.ShouldOffer(fresh, viewer: true, ""));
            TutorialOffers.MarkOffered(fresh, viewer: true, null);
            Assert.IsFalse(TutorialOffers.ShouldOffer(fresh, viewer: true, null));
            Assert.AreEqual(0, fresh.TutorialCopies.Count);

            // The oldest copies are forgotten past the limit
            for (int i = 0; i < TutorialOffers.MAX_COPIES + 5; i++) { TutorialOffers.MarkOffered(fresh, viewer: true, $"c{i}"); }
            Assert.AreEqual(TutorialOffers.MAX_COPIES, fresh.TutorialCopies.Count);
            Assert.IsTrue(TutorialOffers.ShouldOffer(fresh, viewer: true, "c0"));
            Assert.IsFalse(TutorialOffers.ShouldOffer(fresh, viewer: true, $"c{TutorialOffers.MAX_COPIES + 4}"));
            Assert.IsFalse(TutorialOffers.ShouldOffer(null, viewer: false, null));
        }

        [TestMethod]
        public void Settings_DefaultsAndCleaning()
        {
            // An older settings.json: tips on, nothing offered or seen yet
            LaunchSettings older = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>("{\"Msaa\":2,\"WelcomeShown\":true}");
            older.Sanitise();
            Assert.IsTrue(older.Tips);
            Assert.IsFalse(older.TutorialOffered);
            Assert.AreEqual(0, older.TipsSeen.Count);
            Assert.AreEqual(0, older.TutorialCopies.Count);

            var odd = new LaunchSettings
            {
                TipsSeen = new List<string> { " gun-scan ", "", null, "gun-scan", "comments" },
                TutorialChaptersDone = null,
                TutorialCopies = Enumerable.Range(0, TutorialOffers.MAX_COPIES + 3).Select(i => $"id{i}").ToList()
            };
            odd.Sanitise();
            CollectionAssert.AreEqual(new[] { "gun-scan", "comments" }, odd.TipsSeen);
            Assert.AreEqual(0, odd.TutorialChaptersDone.Count);
            Assert.AreEqual(TutorialOffers.MAX_COPIES, odd.TutorialCopies.Count);
            Assert.AreEqual($"id{TutorialOffers.MAX_COPIES + 2}", odd.TutorialCopies[^1]); // the newest kept

            // Round trip
            string json = System.Text.Json.JsonSerializer.Serialize(odd);
            LaunchSettings back = System.Text.Json.JsonSerializer.Deserialize<LaunchSettings>(json);
            back.Sanitise();
            CollectionAssert.AreEqual(odd.TipsSeen, back.TipsSeen);
        }

        [TestMethod]
        public void ViewerCopy_IdRoundTripsAndOlderFilesHaveNone()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            string id = ViewerDto.NewId();
            Assert.AreNotEqual(id, ViewerDto.NewId());
            document.Viewer = new ViewerDto { Id = id };
            string path = folder.File("copy.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(id, read.Viewer.Id);

            Assert.IsNull(new ViewerDto().Copy().Id);
        }

        [TestMethod]
        public void NextStep_IsEnterWhereTheTutorialShowsAndNowhereElse()
        {
            ActionDef next = InputCatalog.Get(InputAction.TutorialNext);
            Assert.AreEqual(InputContext.Tutorial, next.Context);
            Assert.AreEqual("Enter", next.Defaults[0].ToString());
            foreach (string name in new[] { InputCatalog.SIT_WALKING, InputCatalog.SIT_VIEWER, InputCatalog.SIT_HOLDING })
            {
                Assert.IsTrue(InputCatalog.Situation(name).Contains(InputAction.TutorialNext), name);
            }
            foreach (string name in new[] { InputCatalog.SIT_PHOTO, InputCatalog.SIT_MARKUP, InputCatalog.SIT_PAUSE, InputCatalog.SIT_PATH, InputCatalog.SIT_CLASHES })
            {
                Assert.IsFalse(InputCatalog.Situation(name).Contains(InputAction.TutorialNext), name); // Enter means something else there
            }
            Assert.IsTrue(InputCatalog.DefaultsChanges.Any(c => c.Action == InputAction.TutorialNext && c.Version == InputCatalog.DEFAULTS_VERSION));
        }
    }
}
