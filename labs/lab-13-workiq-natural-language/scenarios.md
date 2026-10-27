# Lab 13 scenarios — the "Aurora" incident

Part A sets up demo data in Teams. Part B gives the prompts to send to Otto and the facts you should see in each answer.

Use test accounts only. Below, **Maya Singh** and **Daniel Kim** are roles: post their messages from any two test
people you have (you can be Maya). If you only have one human account, post every message yourself and keep the names
inside the text; the exercises still work.

---

## Part A — Paste-ready demo data

### A1. Create the places

1. In a team you own, create a channel named **`Aurora Incidents`**.
2. Create a group chat named **`Aurora Planning`** with Maya, Daniel and **Otto**.
3. Add **Otto** to the team (Teams → team → *Manage team* → *Add member*), so it can read `Aurora Incidents`.
4. **Do not** create a Project Aurora incident group chat yet. Exercise 7 creates it.

If ambient listening is already on (README "Let Otto listen without @mention (ambient)"), Otto reads these demo posts
too. It should stay silent, because none of them asks it to do anything.

### A2. Post the messages in this order

Post each line as written. **Root** means a new post; **reply** means "Reply" under the root post named in the
Where column.

| # | Author | Where | Message to paste |
|---|---|---|---|
| 1 | Maya | `Aurora Incidents`, **root post A** | `[Old] Aurora checkout errors on Monday. Proposal: restart the payment workers to clear it.` |
| 2 | Daniel | reply to root post A | `Restart did not help last time. Let's not rely on that.` |
| 3 | Maya | `Aurora Incidents`, **root post B** | `INCIDENT: Aurora checkout is failing for about 18% of EU customers since 09:40 UTC. Payments time out after 30 s.` |
| 4 | Daniel | reply to root post B | `Impact: EU region only. US and APAC look healthy. Roughly 1,200 failed orders so far.` |
| 5 | Maya | reply to root post B | `Mitigation in place: traffic shifted 50% to the secondary payment gateway at 10:05 UTC. Error rate down to 6%.` |
| 6 | Daniel | reply to root post B | `Root cause suspect: config change cfg-1042 deployed at 09:35 UTC to the payment client timeout.` |
| 7 | Maya | reply to root post B | `Option 1: restart workers. Option 2: roll back cfg-1042 to cfg-1041.` |
| 8 | Daniel | reply to root post B | `FINAL DECISION (approved by Daniel Kim): roll back cfg-1042 to cfg-1041. Restart is NOT the fix. Rollback window 10:30–10:45 UTC.` |
| 9 | Maya | reply to root post B | `Rollback done at 10:41 UTC. Error rate 0.3% and falling. Monitoring for one hour.` |
| 10 | Maya | `Aurora Planning` chat | `Who owns what for Aurora today?` |
| 11 | Daniel | `Aurora Planning` chat | `SME for the payment client is Priya Nair. I'm incident commander. Maya handles customer comms.` |
| 12 | Maya | `Aurora Planning` chat | `Action: Maya posts a customer update by 12:00 UTC. Daniel writes the postmortem draft by Friday.` |
| 13 | Daniel | `Aurora Planning` chat | `Reminder: the restart idea from Monday is superseded. Rollback is the decision of record.` |

### A3. Copy the links you will paste later

- **Group chat link:** in `Aurora Planning`, hover a message → `…` → *Copy link*.
- **Channel thread link:** on root post B → `…` → *Copy link*.

Wait **15–30 minutes** after posting before Exercise 2 (search indexing). Exercises 3 and 4 can usually run sooner.

### A4. Facts sheet (what correct answers contain)

| Topic | Correct fact | Common wrong answer to watch for |
|---|---|---|
| Impact | EU only, ~18% of checkouts, ~1,200 failed orders, since 09:40 UTC | "All regions" |
| Mitigation | 50% traffic to secondary gateway at 10:05 UTC | — |
| Root cause suspect | cfg-1042 timeout change at 09:35 UTC | — |
| Final decision | **Roll back cfg-1042 → cfg-1041**, approved by Daniel Kim | "Restart workers" (old, superseded) |
| Outcome | Rollback done 10:41 UTC, error rate 0.3% | — |
| SME | **Priya Nair** (payment client) | — |
| Roles | Daniel = incident commander; Maya = customer comms | — |
| Actions | Maya: customer update by 12:00 UTC; Daniel: postmortem draft by Friday | — |

---

## Part B — Exercises

| PM requirement | Exercises |
|---|---|
| Grounding: general | 2 |
| Grounding: scoped to a chat or channel | 3, 4 |
| Confirmation before sharing in groups | 5, 6 |
| Taking actions | 7 |
| Ambient listening (intent-based, confirm in the group before acting) | 8 |
| 1:1 responses | 1, 2, 3 |
| Adaptive Cards in notifications | 9 |
| Streaming (1:1) | 10 |

Send each prompt as written. In a group chat or channel, start the message with **@Otto** (pick Otto from the mention list).

### 1. Capabilities (1:1 chat)

> What can you do with WorkIQ? List the tools you have and what each one is for.

Expect: tool names from the live WorkIQ catalog (for example `ask`, `retrieve`, `fetch`, `create_entity`) and a note that
Otto acts as itself, so it sees only what Otto has access to.

### 2. General grounded question (1:1 chat)

> What was the final decision on the Aurora checkout incident, and who is the SME?

Expect: rollback cfg-1042 → cfg-1041 (approved by Daniel Kim), **not** the restart; SME Priya Nair. If Otto says it
can't find anything, the data may not be indexed yet — wait, or use Exercise 3/4.

### 3. Scoped to one chat (1:1 chat)

> Using only this chat: `<paste the Aurora Planning link>` — list each person's responsibility and the action items with deadlines.

Expect: Daniel = incident commander + postmortem by Friday; Maya = customer comms + update by 12:00 UTC; Priya = SME.
Otto should not mix in facts that only exist in the channel.

### 4. Scoped to the channel thread (reply inside root post B)

> @Otto summarize this thread: impact, mitigation, final decision, current status.

Expect: the facts-sheet rows Impact → Outcome, posted as a reply in the **same thread**. The old root post A restart idea
should be called superseded or left out.

### 5. Private confirmation (reply inside root post B)

> @Otto Start a Project Aurora incident group chat with Maya Singh and Daniel Kim, then post a concise summary of the current incident, its mitigation, and who the SME is

Expect in the thread: a short note that this would create a group chat with Maya Singh and Daniel Kim and post a summary, and that Otto will confirm with you
in your 1:1 chat. Nothing is created yet.

### 6. Approve, decline or revise (your 1:1 chat with Otto)

Open your 1:1 chat with Otto. Try one of:

> What are you planning to do for my Aurora request?

> Change the summary to also mention that Maya posts the next customer update by 12:00 UTC.

> Cancel that request.

Expect: Otto recalls the channel request (it keeps it in memory for your account), shows the exact members (Maya Singh, Daniel Kim) and the
summary it plans to post (incident, mitigation: rollback cfg-1042 → cfg-1041, SME: Priya Nair), and does nothing until
you approve. After *Cancel*, it does nothing.

### 7. Create the group and post the summary (your 1:1 chat)

> Approved. Go ahead.

Expect: Otto creates the Project Aurora incident group chat with Maya Singh and Daniel Kim and posts the summary
(incident, mitigation, SME Priya Nair), then tells you what it did. Open Teams and **check yourself** that the chat exists
with those members and that message. If Otto replies *Sorry, something went wrong*,
check Teams before asking again — Otto never repeats a write automatically.

Tenant policy can block group creation or posting by an agent user; Otto should then report WorkIQ's error rather than
pretend it worked.

### 8. Ambient: incident discussion → Otto proposes, you confirm, Otto posts the ICM summary (`Aurora Planning` chat)

Prerequisites:

- The ambient `PUT` from the README section "Let Otto listen without @mention (ambient)" is done, and the `GET` shows
  `allMessages` for group chats with your current tunnel `callbackUri`.
- Otto is a member of the `Aurora Planning` chat and of the team that owns `Aurora Incidents`.

Post these in `Aurora Planning` **without** @mentioning Otto. Otto should stay silent for messages 1–5.

| # | Author | Message to paste |
|---|---|---|
| 1 | Maya | `Heads up: ICM 123456789 just fired, Sev2. Aurora search API returns 503 for about 30% of requests since 14:10 UTC.` |
| 2 | Daniel | `Impact is EU and UK tenants only. Customers can't search orders; checkout still works.` |
| 3 | Maya | `Looks like the search cache cluster lost two nodes after the 14:00 patch.` |
| 4 | Daniel | `Mitigation: I failed search over to the backup cache at 14:35 UTC. Errors are down to 2%.` |
| 5 | Maya | `I own the ICM. Priya Nair is the SME for the cache cluster. Next steps: rebuild the two nodes and confirm the patch before closing.` |
| 6 | Daniel | `We need to create a post about this ICM in Aurora Incidents` |

Expect after message 6: Otto reads the chat through WorkIQ and replies **in the chat** with a proposal. It names the
target channel (`Aurora Incidents`), shows a short draft and ends with "Reply yes to go ahead." Nothing is posted yet.

Then reply `yes` in the chat, **without** @mentioning Otto. (Reply `no` to drop it, or ask for a change to see a revised
proposal.) Otto then posts **one** new post in `Aurora Incidents`, similar to:

> **ICM 123456789 — Aurora search API returning 503s (Sev2)**
> **Impact:** ~30% of search requests fail for EU and UK tenants since 14:10 UTC; checkout unaffected.
> **Mitigation:** search failed over to the backup cache at 14:35 UTC; errors down to 2%.
> **Suspected cause:** search cache cluster lost two nodes after the 14:00 patch.
> **Owner:** Maya Singh · **SME:** Priya Nair
> **Next steps:** rebuild the two cache nodes; confirm the patch before closing.

Otto then replies in the chat with one short line saying where it posted. The confirmation happens in the group chat,
not in 1:1. Check `Aurora Incidents` yourself. If Otto apologises with an error instead, check the channel before asking again,
because the post may already exist.

Bonus: post `Otto, what WorkIQ tools do you have?` without @mentioning him. Otto answers directly, because the question is
aimed at him and is read-only. Plain chat between Maya and Daniel still gets no reply.

### 9. Adaptive Card notifications (1:1, `Aurora Planning` chat, `Aurora Incidents` channel)

Use a second person as the **recipient**, with Teams minimized or open in another chat. In each of the three places, post:

```text
@Otto send me the Aurora status as an Adaptive Card
```

(In 1:1 no @mention is needed; the recipient is you, so check your own notifications.) In the group chat and the channel
the card is posted for everyone in that conversation.

| Check | Pass |
|---|---|
| In the conversation | One card from Otto with the Aurora facts (rollback cfg-1042 → cfg-1041 done, error rate 0.3%, SME Priya Nair). No extra text reply. |
| Desktop toast | Shows **Otto** and the one-line summary, not "Sent a card" or empty text |
| Activity feed / chat-list preview | Same summary text |
| Mobile push | Same summary text |
| Console | `[card] to=/chats/…/messages status=ok` (or `/teams/…/channels/…`), then `[agent] NO_REPLY (already posted via WorkIQ)` |

Record which surfaces show the summary and which do not. That is the finding for "Adaptive Cards in Notifications".
The card is display-only, so it has no buttons that call back to Otto.

### 10. Streaming (1:1 chat)

Post this slow, multi-source question in your 1:1 with Otto:

```text
Summarize everything about the Aurora incident across mail, chats and files
```

| Check | Pass |
|---|---|
| First status | "Working on it…" appears within about 1 second |
| Status updates | Changes as Otto works, e.g. "Searching mail…", "Reading Teams messages…", "Looking at files…" |
| Answer | Streams in, then ends as **one** final message (no duplicate) |
| Console | `[stream] start`, `[stream] status "…" … id=<id>`, several `[stream] chunk …`, `[stream] final chars=<n> … id=<id>` |

If the console shows `[stream] failed: …`, Otto fell back to one normal reply. Note the client (iOS streaming is still rolling out). If the console shows `past the 2-minute stream limit`, the WorkIQ work took over 2 minutes; the answer arrives as one normal message. In group chats and channels Otto doesn't stream; he sends one normal reply.

### Clean up

Delete the test group chat and the `Aurora Incidents` posts when you are done, or keep them for a demo.
