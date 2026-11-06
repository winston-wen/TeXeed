using System.IO;
using TeXeed.App.AI;

internal static partial class Program
{
    private static void CheckAiGlobalCarry(string output)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var priorHome = Environment.GetEnvironmentVariable("TEXEED_HOME");
        var isolated = Path.Combine(output, "global-carry-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TEXEED_HOME", isolated);
        try
        {
            var projectA = Path.Combine(isolated, "A", "main.tex"); var projectB = Path.Combine(isolated, "B", "main.tex");
            var a = new AiConversation { Id = "a", Title = "研究 A" }; var b = new AiConversation { Id = "b", Title = "研究 B" };
            a.Turns.Add(new AiTurn("甲的问题", "甲的回答") { Id = "a1" }); b.Turns.Add(new AiTurn("乙的问题", "乙的回答") { Id = "b1" });
            a.CommentCarry["a1"] = "temporary"; b.CommentCarry["b1"] = "long";
            a.SharedDocument.Comments.Add(new AiDocumentComment { Id = "ca", RoundId = "a1", Quote = "甲", Text = "请核查甲" });
            b.SharedDocument.Comments.Add(new AiDocumentComment { Id = "cb", RoundId = "b1", Quote = "乙", Text = "请核查乙" });
            Check(AiGlobalCarry.Synchronize(projectA, a) && AiGlobalCarry.Synchronize(projectB, b), "initial publication failed");
            Check(AiGlobalCarry.List().Count == 2, "cross-project conversations missing");
            var old = AiGlobalCarry.List().Single(c => c.ConversationId == "a").Rounds.Single();
            AiGlobalCarry.SetMode(projectA, "a", "a1", "temporary");
            Check(!AiGlobalCarry.ConsumeTemporary(projectA, "a", "a1", old.SelectionRevision), "old send consumed newly reselected temporary state");
            var current = AiGlobalCarry.List().Single(c => c.ConversationId == "a").Rounds.Single();
            Check(AiGlobalCarry.ConsumeTemporary(projectA, "a", "a1", current.SelectionRevision), "temporary selection was not consumed");
            AiGlobalCarry.SetMode(projectB, "b", "b1", "none");
            AiGlobalCarry.Synchronize(projectB, b);
            Check(b.CommentCarry["b1"] == "none", "external cancellation was overwritten by an old local selection");
            AiGlobalCarry.SetMode(projectB, "b", "b1", "long"); AiGlobalCarry.Synchronize(projectB, b);
            var expected = AiGlobalCarry.List().Single(c => c.ConversationId == "b").Comments.Single();
            var reply = new AiDocumentReply { Id = "reply-1", Author = "model-a", Role = "ai", Text = "应补充条件。" };
            Check(AiGlobalCarry.TryAppendReply(projectB, "b", expected, reply), "cross-project reply was not persisted");
            Check(!AiGlobalCarry.TryAppendReply(projectB, "b", expected, reply), "duplicate or stale reply was accepted");
            Check(AiGlobalCarry.Synchronize(projectB, b), "reply could not merge into original conversation");
            Check(b.SharedDocument.Comments.Single().Replies.Single().Id == "reply-1", "reply was not merged into original conversation");
            AiGlobalCarry.Synchronize(projectB, b);
            Check(b.SharedDocument.Comments.Single().Replies.Count == 1, "reply was applied twice");
            var bBook = new AiNotebook { Conversations = [b], ActiveConversationId = b.Id }; AiStorage.Save(projectB, bBook); AiGlobalCarry.AcknowledgePersisted(projectB, b.Id);
            var beforeEdit = AiGlobalCarry.List().Single(c => c.ConversationId == "b").Comments.Single();
            var pending = new AiDocumentReply { Id = "reply-2", Author = "model-b", Role = "ai", Text = "旧上下文的回复。" };
            Check(AiGlobalCarry.TryAppendReply(projectB, "b", beforeEdit, pending), "could not queue reply");
            b.SharedDocument.Comments.Single().Text = "用户并发修改";
            Check(!AiGlobalCarry.Synchronize(projectB, b), "concurrent comment change was not detected");
            Check(b.SharedDocument.Comments.Single().Text == "用户并发修改" && b.SharedDocument.Comments.Single().Replies.Count == 1, "pending reply overwrote edited comment");
            Check(AiGlobalCarry.List().Single(c => c.ConversationId == "b").PendingReplies.Single().Reply.Id == "reply-2", "conflicting reply was lost");
            b.SharedDocument.Comments.Clear();
            Check(!AiGlobalCarry.Synchronize(projectB, b) && b.SharedDocument.Comments.Count == 0, "deleted comment was restored");
            var projectC = Path.Combine(isolated, "C", "main.tex"); var projectD = Path.Combine(isolated, "D", "main.tex");
            var c = new AiConversation { Id = "c" }; var d = new AiConversation { Id = "d" };
            c.Turns.Add(new AiTurn("c", "") { Id = "c1" }); d.Turns.Add(new AiTurn("d", "") { Id = "d1" });
            c.SharedDocument.Comments.Add(new AiDocumentComment { Id = "cc", Text = "原评论 C" }); d.SharedDocument.Comments.Add(new AiDocumentComment { Id = "cd", Text = "原评论 D" });
            AiGlobalCarry.Synchronize(projectC, c); AiGlobalCarry.Synchronize(projectD, d);
            var recordC = AiGlobalCarry.List().Single(x => x.ConversationId == "c"); var recordD = AiGlobalCarry.List().Single(x => x.ConversationId == "d");
            var firstUpdate = new AiGlobalReplyUpdate(projectC, "c", recordC.Comments.Single(), new AiDocumentReply { Id = "rc", Text = "回复 C" });
            var stale = recordD.Comments.Single().Snapshot(); stale.Text = "旧评论 D";
            var failedUpdate = new AiGlobalReplyUpdate(projectD, "d", stale, new AiDocumentReply { Id = "rd", Text = "回复 D" });
            var before = a.SharedDocument.Snapshot(); var after = before.Snapshot(); after.Comments.Single().Text = "本地修改已落盘";
            var local = new AiGlobalLocalChange("a", before, after); var aBook = new AiNotebook { Conversations = [a], ActiveConversationId = a.Id };
            AiStorage.Save(projectA, aBook);
            Check(AiGlobalCarry.PrepareReplies(projectA, [firstUpdate, failedUpdate], [local]) is null, "invalid batch prepared");
            Check(AiGlobalCarry.List().Single(x => x.ConversationId == "c").Comments.Single().Replies.Count == 0, "invalid batch wrote a partial reply");
            var secondUpdate = failedUpdate with { ExpectedComment = recordD.Comments.Single() };
            Task<AiNotebook>? blockedLoad = null;
            using (var prepared = AiGlobalCarry.PrepareReplies(projectA, [firstUpdate, secondUpdate], [local]) ?? throw new Exception("valid batch rejected"))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew(); var busy = false; try { AiGlobalCarry.List(); } catch (AiGlobalCarryBusyException) { busy = true; }
                Check(busy && watch.ElapsedMilliseconds < 200, "global lock blocked another UI window");
                blockedLoad = AiStorage.LoadAsync(projectA); Thread.Sleep(50); Check(!blockedLoad.IsCompleted, "project load did not retry a busy global transaction");
                var rejected = false; try { prepared.CommitAfterSave(); } catch (IOException) { rejected = true; }
                Check(rejected, "remote replies were committed before local notebook save");
                Check(a.SharedDocument.TryRestore(before, after), "could not apply local snapshot"); AiStorage.Save(projectA, aBook); prepared.CommitAfterSave();
            }
            Check(blockedLoad!.GetAwaiter().GetResult().Active.SharedDocument.Comments.Single().Text == "本地修改已落盘", "project load did not resume after lock release");
            Check(AiGlobalCarry.Synchronize(projectC, c) && AiGlobalCarry.Synchronize(projectD, d), "batch replies did not merge");
            Check(c.SharedDocument.Comments.Single().Replies.Single().Id == "rc" && d.SharedDocument.Comments.Single().Replies.Single().Id == "rd", "batch reply missing or duplicated");
            var oldA = a.SharedDocument.Snapshot(); var newA = oldA.Snapshot(); newA.Comments.Single().Text = "崩溃后恢复";
            var priorC = AiGlobalCarry.List().Single(x => x.ConversationId == "c").Comments.Single();
            var recoveryUpdate = new AiGlobalReplyUpdate(projectC, "c", priorC, new AiDocumentReply { Id = "rc2", Text = "恢复后的回复" });
            using (AiGlobalCarry.PrepareReplies(projectA, [recoveryUpdate], [new AiGlobalLocalChange("a", oldA, newA)]) ?? throw new Exception("recovery transaction rejected")) { }
            var reloaded = AiStorage.Load(projectA);
            Check(AiGlobalCarry.RecoverNotebook(projectA, reloaded) == 0 && reloaded.Active.SharedDocument.Comments.Single().Text == "崩溃后恢复", "prepared local transaction was not restored");
            Check(AiGlobalCarry.Synchronize(projectC, c) && c.SharedDocument.Comments.Single().Replies.Count == 2, "recovered external reply missing");
            var blockedDelete = false; try { AiGlobalCarry.Remove(projectC, "c"); } catch (InvalidOperationException) { blockedDelete = true; }
            Check(blockedDelete, "pending reply was silently deleted");
            var projectE = Path.Combine(isolated, "E", "main.tex"); var e = new AiConversation { Id = "e", Title = "等待回帖" };
            e.Turns.Add(new AiTurn("e", "") { Id = "e1" }); e.SharedDocument.TryInitialize("待研究", "model-old");
            e.SharedDocument.Comments.Add(new AiDocumentComment { Id = "ce", Text = "能解释吗", Quote = "待", Length = 1, RoundId = "e1" });
            var eBook = new AiNotebook { Conversations = [e], ActiveConversationId = e.Id }; AiStorage.Save(projectE, eBook); AiGlobalCarry.Synchronize(projectE, e);
            var expectedE = AiGlobalCarry.List().Single(x => x.ConversationId == "e").Comments.Single();
            Check(AiGlobalCarry.TryAppendReply(projectE, "e", expectedE, new AiDocumentReply { Id = "re", Author = "model-real", Role = "ai", Text = "这是解释。" }), "reply to closed project failed");
            var openedE = AiStorage.Load(projectE);
            Check(openedE.Active.SharedDocument.Comments.Single().Replies.Single().Id == "re", "closed source conversation did not automatically show reply");
            Check(AiGlobalCarry.List().Single(x => x.ConversationId == "e").PendingReplies.Count == 0, "durable reply was not acknowledged");
            var versionsE = AiDocumentRepository.ListVersionsAsync(projectE, "e").GetAwaiter().GetResult();
            Check(versionsE.Count > 0 && versionsE[0].Author == "model-real", "source Git history omitted model reply");
            var currentA = AiGlobalCarry.List().Single(x => x.ConversationId == "a").Rounds.Single();
            AiGlobalCarry.Remove(projectA, "a"); Check(AiGlobalCarry.List().All(x => x.ConversationId != "a"), "deleted session still carried");
            _ = AiStorage.Load(projectA);
            Check(AiGlobalCarry.List().Single(x => x.ConversationId == "a").Rounds.Single().Mode == currentA.Mode, "failed deletion did not recover external selection");
            var projectG = Path.Combine(isolated, "G", "main.tex"); var g = new AiConversation { Id = "g" };
            g.Turns.Add(new AiTurn("g", "") { Id = "g1" }); g.SharedDocument.TryInitialize("原文", "model"); g.SharedDocument.Comments.Add(new AiDocumentComment { Id = "cg", Text = "问题" });
            var gBook = new AiNotebook { Conversations = [g], ActiveConversationId = g.Id }; AiStorage.Save(projectG, gBook); AiGlobalCarry.Synchronize(projectG, g);
            var expectedG = AiGlobalCarry.List().Single(x => x.ConversationId == "g").Comments.Single();
            Check(AiGlobalCarry.TryAppendReply(projectG, "g", expectedG, new AiDocumentReply { Id = "rg", Author = "model", Text = "回复" }), "failed to prepare Git failure fixture");
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", isolated);
                var noGit = AiStorage.Load(projectG);
                Check(noGit.CarryRecoveryConflicts > 0 && noGit.Active.SharedDocument.Comments.Single().Replies.Single().Id == "rg", "Git failure prevented reading or hid warning");
                Check(AiGlobalCarry.List().Single(x => x.ConversationId == "g").PendingReplies.Count == 1, "Git failure discarded pending reply");
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
            var retried = AiStorage.Load(projectG);
            Check(retried.Active.SharedDocument.Comments.Single().Replies.Count == 1 && AiGlobalCarry.List().Single(x => x.ConversationId == "g").PendingReplies.Count == 0, "Git recovery lost or duplicated reply");
            var projectF = Path.Combine(isolated, "F", "main.tex"); var f = new AiConversation { Id = "f" };
            f.Turns.Add(new AiTurn("f", "") { Id = "f1" }); f.SharedDocument.TryInitialize("原文", "model");
            var fBook = new AiNotebook { Conversations = [f], ActiveConversationId = f.Id }; AiStorage.Save(projectF, fBook); AiGlobalCarry.Synchronize(projectF, f);
            var beforeF = f.SharedDocument.Snapshot(); var afterF = beforeF.Snapshot(); afterF.TryApply(afterF.Revision, "准备中的结果", "model", "ai");
            var conflictRef = AiGlobalCarry.List().Single(x => x.ConversationId == "e").Comments.Single();
            var conflictUpdate = new AiGlobalReplyUpdate(projectE, "e", conflictRef, new AiDocumentReply { Id = "conflicted-reply", Author = "model", Role = "ai", Text = "等待恢复" });
            using (AiGlobalCarry.PrepareReplies(projectF, [conflictUpdate], [new AiGlobalLocalChange("f", beforeF, afterF)]) ?? throw new Exception("conflict test prepare failed")) { }
            f.SharedDocument.TryApply(f.SharedDocument.Revision, "用户后来修改", "用户"); AiStorage.Save(projectF, fBook);
            var conflicted = AiStorage.Load(projectF);
            Check(conflicted.CarryRecoveryConflicts == 1 && conflicted.Active.SharedDocument.Source == "用户后来修改", "recovery overwrote later user edits or omitted warning");
            blockedDelete = false; try { AiGlobalCarry.Remove(projectF, "f"); } catch (InvalidOperationException) { blockedDelete = true; }
            Check(blockedDelete, "prepared recovery was silently removed");
            Parallel.For(0, 20, i =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { AiGlobalCarry.SetMode(i % 2 == 0 ? projectA : projectB, i % 2 == 0 ? "a" : "b", i % 2 == 0 ? "a1" : "b1", i % 2 == 0 ? "long" : "none"); break; }
                    catch (AiGlobalCarryBusyException) when (attempt < 1000) { Thread.Sleep(2); }
                }
            });
            var all = AiGlobalCarry.List();
            Check(all.Count == 7 && all.Single(c => c.ConversationId == "a").Rounds.Single().Mode == "long" && all.Single(c => c.ConversationId == "b").Rounds.Single().Mode == "none", "parallel project selections were lost");
            Console.WriteLine("PASS global carry selection, CAS consumption, cross-project reply and concurrent edits");
        }
        finally { Environment.SetEnvironmentVariable("TEXEED_HOME", priorHome); }
    }
}
