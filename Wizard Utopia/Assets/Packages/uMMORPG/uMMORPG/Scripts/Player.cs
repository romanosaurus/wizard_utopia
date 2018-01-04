// All player logic was put into this class. We could also split it into several
// smaller components, but this would result in many GetComponent calls and a
// more complex syntax.
//
// The default Player class takes care of the basic player logic like the state
// machine and some properties like damage and defense.
//
// The Player class stores the maximum experience for each level in a simple
// array. So the maximum experience for level 1 can be found in expMax[0] and
// the maximum experience for level 2 can be found in expMax[1] and so on. The
// player's health and mana are also level dependent in most MMORPGs, hence why
// there are hpMax and mpMax arrays too. We can find out a players's max health
// in level 1 by using hpMax[0] and so on.
//
// The class also takes care of selection handling, which detects 3D world
// clicks and then targets/navigates somewhere/interacts with someone.
//
// Animations are not handled by the NetworkAnimator because it's still very
// buggy and because it can't really react to movement stops fast enough, which
// results in moonwalking. Not synchronizing animations over the network will
// also save us bandwidth.
//
// Note: unimportant commands should use the Unreliable channel to reduce load.
// (it doesn't matter if a player has to click the respawn button twice if under
//  heavy load)
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_5_5_OR_NEWER // for people that didn't upgrade to 5.5. yet
using UnityEngine.AI;
#endif
using System;
using System.Linq;
using System.Collections.Generic;

public enum TradeStatus {Free, Locked, Accepted};

[Serializable]
public struct SkillbarEntry {
    public string reference;
    public KeyCode hotKey;
};

[Serializable]
public struct EquipmentInfo {
    public string requiredCategory;
    public Transform location;
    public ItemTemplate defaultItem;
}

[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(Chat))]
[RequireComponent(typeof(NetworkName))]
public partial class Player : Entity {
    [Header("Components")]
    public Chat chat;

    [Header("Text Meshes")]
    public TextMesh guildOverlay;
    public string guildOverlayPrefix = "[";
    public string guildOverlaySuffix = "]";
    public Color nameOverlayDefaultColor = Color.white;
    public Color nameOverlayOffenderColor = Color.magenta;
    public Color nameOverlayMurdererColor = Color.red;
    public Color nameOverlayPartyColor = new Color(0.341f, 0.965f, 0.702f);

    [Header("Icons")]
    public Sprite classIcon; // for character selection

    // some meta info
    [HideInInspector] public string account = "";
    [HideInInspector] public string className = "";

    // level based stats
    [System.Serializable]
    public partial class PlayerLevel {
        public int healthMax = 100;
        public int manaMax = 100;
        public long experienceMax = 10;
        public int baseDamage = 1;
        public int baseDefense = 1;
        [Range(0, 1)] public float baseBlockChance;
        [Range(0, 1)] public float baseCriticalChance;
    }
    [Header("Level based Stats")]
    public PlayerLevel[] levels = { new PlayerLevel() }; // default

    // health
    public override int healthMax {
        get {
            // calculate equipment bonus
            int equipmentBonus = (from item in equipment
                                  where item.valid
                                  select item.equipHealthBonus).Sum();

            // calculate buff bonus
            int buffBonus = (from skill in skills
                             where skill.BuffTimeRemaining() > 0
                             select skill.buffsHealthMax).Sum();

            // calculate strength bonus (1 strength means 1% of hpMax bonus)
            int attributeBonus = Convert.ToInt32(levels[level-1].healthMax * (strength * 0.01f));

            // return base + attribute + equip + buffs
            return levels[level-1].healthMax + equipmentBonus + buffBonus + attributeBonus;
        }
    }

    // mana
    public override int manaMax {
        get {
            // calculate equipment bonus
            int equipmentBonus = (from item in equipment
                                  where item.valid
                                  select item.equipManaBonus).Sum();

            // calculate buff bonus
            int buffBonus = (from skill in skills
                             where skill.BuffTimeRemaining() > 0
                             select skill.buffsManaMax).Sum();

            // calculate intelligence bonus (1 intelligence means 1% of hpMax bonus)
            int attributeBonus = Convert.ToInt32(levels[level-1].manaMax * (intelligence * 0.01f));

            // return base + attribute + equip + buffs
            return levels[level-1].manaMax + equipmentBonus + buffBonus + attributeBonus;
        }
    }

    // damage
    public int baseDamage { get { return levels[level-1].baseDamage; } }
    public override int damage {
        get {
            // calculate equipment bonus
            int equipmentBonus = (from item in equipment
                                  where item.valid
                                  select item.equipDamageBonus).Sum();

            // calculate buff bonus
            int buffBonus = (from skill in skills
                             where skill.BuffTimeRemaining() > 0
                             select skill.buffsDamage).Sum();

            // return base + equip + buffs
            return baseDamage + equipmentBonus + buffBonus;
        }
    }

    // defense
    public int baseDefense { get { return levels[level-1].baseDefense; } }
    public override int defense {
        get {
            // calculate equipment bonus
            int equipmentBonus = (from item in equipment
                                  where item.valid
                                  select item.equipDefenseBonus).Sum();

            // calculate buff bonus
            int buffBonus = (from skill in skills
                             where skill.BuffTimeRemaining() > 0
                             select skill.buffsDefense).Sum();

            // return base + equip + buffs
            return baseDefense + equipmentBonus + buffBonus;
        }
    }

    // block
    public float baseBlockChance { get { return levels[level-1].baseBlockChance; } }
    public override float blockChance {
        get {
            // calculate equipment bonus
            float equipmentBonus = (from item in equipment
                                    where item.valid
                                    select item.equipBlockChanceBonus).Sum();

            // calculate buff bonus
            float buffBonus = (from skill in skills
                               where skill.BuffTimeRemaining() > 0
                               select skill.buffsBlockChance).Sum();

            // return base + equip + buffs
            return baseBlockChance + equipmentBonus + buffBonus;
        }
    }

    // crit
    public float baseCriticalChance { get { return levels[level-1].baseCriticalChance; } }
    public override float criticalChance {
        get {
            // calculate equipment bonus
            float equipmentBonus = (from item in equipment
                                    where item.valid
                                    select item.equipCriticalChanceBonus).Sum();

            // calculate buff bonus
            float buffBonus = (from skill in skills
                               where skill.BuffTimeRemaining() > 0
                               select skill.buffsCriticalChance).Sum();

            // return base + equip + buffs
            return baseCriticalChance + equipmentBonus + buffBonus;
        }
    }

    [Header("Attributes")]
    [SyncVar] public int strength = 0;
    [SyncVar] public int intelligence = 0;

    [Header("Experience")] // note: int is not enough (can have > 2 mil. easily)
    [SyncVar, SerializeField] long _experience = 0;
    public long experience {
        get { return _experience; }
        set {
            if (value <= _experience) {
                // decrease
                _experience = Math.Max(value, 0);
            } else {
                // increase with level ups
                // set the new value (which might be more than expMax)
                _experience = value;

                // now see if we leveled up (possibly more than once too)
                // (can't level up if already max level)
                while (_experience >= experienceMax && level < levels.Length) {
                    // subtract current level's required exp, then level up
                    _experience -= experienceMax;
                    ++level;

                    // addon system hooks
                    Utils.InvokeMany(typeof(Player), this, "OnLevelUp_");
                }

                // set to expMax if there is still too much exp remaining
                if (_experience > experienceMax) _experience = experienceMax;
            }
        }
    }
    public long experienceMax { get { return levels[level-1].experienceMax; } }

    [Header("Skill Experience")]
    [SyncVar] public long skillExperience = 0;

    [Header("Indicator")]
    public GameObject indicatorPrefab;
    GameObject indicator;

    [Header("Inventory")]
    public int inventorySize = 30;
    public ItemTemplate[] defaultItems;
    public KeyCode[] inventorySplitKeys = {KeyCode.LeftShift, KeyCode.RightShift};

    [Header("Trash")]
    [SyncVar] public Item trash = new Item();

    [Header("Equipment")]
    public EquipmentInfo[] equipmentInfo = new EquipmentInfo[] {
        new EquipmentInfo{requiredCategory="EquipmentWeapon", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentHead", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentChest", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentLegs", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentShield", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentShoulders", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentHands", location=null, defaultItem=null},
        new EquipmentInfo{requiredCategory="EquipmentFeet", location=null, defaultItem=null}
    };
    public SyncListItem equipment = new SyncListItem();

    [Header("Skillbar")]
    public SkillbarEntry[] skillbar = new SkillbarEntry[]
    {
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha1},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha2},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha3},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha4},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha5},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha6},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha7},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha8},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha9},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha0},
    };

    [Header("Quests")] // contains active and completed quests (=all)
    public int questLimit = 10;
    public SyncListQuest quests = new SyncListQuest();

    [Header("Interaction")]
    public float interactionRange = 4;
    public KeyCode targetNearestKey = KeyCode.Tab;
    public bool localPlayerClickThrough = true; // click selection goes through localplayer. feels best.

    [Header("Trading")]
    [SyncVar, HideInInspector] public string tradeRequestFrom = "";
    [SyncVar, HideInInspector] public TradeStatus tradeStatus = TradeStatus.Free;
    [SyncVar, HideInInspector] public long tradeOfferGold = 0;
    public SyncListInt tradeOfferItems = new SyncListInt(); // inventory indices

    [Header("Crafting")]
    public List<int> craftingIndices = Enumerable.Repeat(-1, RecipeTemplate.recipeSize).ToList();

    [Header("Item Mall")]
    [SyncVar] public long coins = 0;
    public float couponWaitSeconds = 3;

    [Header("Guild")]
    [SyncVar, HideInInspector] public string guildName = ""; // syncvar so that all observers see it
    [SyncVar, HideInInspector] public string guildInviteFrom = "";
    [HideInInspector] public Guild guild; // no syncvar, only owner sees it via TargetRPC (saves lots of bandwidth)
    public float guildInviteWaitSeconds = 3;

    [Header("Party")]
    [HideInInspector] public Party party;
    [SyncVar, HideInInspector] public string partyInviteFrom = "";
    public float partyInviteWaitSeconds = 3;

    [Header("Pet")]
    [SyncVar] GameObject _activePet;
    public Pet activePet {
        get { return _activePet != null  ? _activePet.GetComponent<Pet>() : null; }
        set { _activePet = value != null ? value.gameObject : null; }
    }
    // pet's destination should always be right next to player, not inside him
    // -> we use a helper property so we don't have to recalculate it each time
    // -> we offset the position by exactly 1 x bounds to the left because dogs
    //    are usually trained to walk on the left of the owner. looks natural.
    public Vector3 petDestination {
        get {
            Bounds bounds = collider.bounds;
            return transform.position - transform.right * bounds.size.x;
        }
    }

    [Header("Death")]
    public float deathExperienceLossPercent = 0.05f;

    // some commands should have delays to avoid DDOS, too much database usage
    // or brute forcing coupons etc. we use one riskyAction timer for all.
    [SyncVar, HideInInspector] public float nextRiskyActionTime = 0;

    // the next skill to be set if we try to set it while casting
    int nextSkill = -1;

    // the next target to be set if we try to set it while casting
    Entity nextTarget;

    // cache players to save lots of computations
    // (otherwise we'd have to iterate NetworkServer.objects all the time)
    // => on server: all online players
    // => on client: all observed players
    public static Dictionary<string, Player> onlinePlayers = new Dictionary<string, Player>();

    // networkbehaviour ////////////////////////////////////////////////////////
    protected override void Awake() {
        // cache base components
        base.Awake();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "Awake_");
    }

    public override void OnStartLocalPlayer() {
        // setup camera targets
        Camera.main.GetComponent<CameraMMO>().target = transform;
        GameObject.FindWithTag("MinimapCamera").GetComponent<CopyPosition>().target = transform;

        // load skillbar after player data was loaded
        LoadSkillbar();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnStartLocalPlayer_");
    }

    public override void OnStartServer() {
        base.OnStartServer();

        // initialize trade item indices
        for (int i = 0; i < 6; ++i) tradeOfferItems.Add(-1);

        InvokeRepeating("ProcessCoinOrders", 5, 5);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnStartServer_");
    }

    protected override void Start() {
        base.Start();
        onlinePlayers[name] = this;

        if (isClient) {
            // setup synclist callbacks on client. no need to update and show and
            // animate equipment on server
            equipment.Callback += OnEquipmentChanged;

            // refresh all locations once (on synclist changed won't be called
            // for initial lists)
            for (int i = 0; i < equipment.Count; ++i)
                RefreshLocation(i);
        }

        // spawn effects for any buffs that might still be active after loading
        // (OnStartServer is too early)
        // note: no need to do that in Entity.Start because we don't load them
        //       with previously casted skills
        if (isServer)
            for (int i = 0; i < skills.Count; ++i)
                if (skills[i].BuffTimeRemaining() > 0)
                    SpawnSkillEffect(i, this);

        // notify guild members that we are online. this also updates the client's
        // own guild info via targetrpc automatically
        // -> OnStartServer is too early because it's not spawned there yet
        if (isServer)
            SetGuildOnline(true);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "Start_");
    }

    void LateUpdate() {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => only play moving animation while the agent is actually moving. the
        //    MOVING state might be delayed to due latency or we might be in
        //    MOVING while a path is still pending, etc.
        if (isClient) { // no need for animations on the server
            // now pass parameters after any possible rebinds
            foreach (var anim in GetComponentsInChildren<Animator>()) {
                anim.SetBool("MOVING", state == "MOVING" && agent.velocity != Vector3.zero);
                anim.SetBool("CASTING", state == "CASTING");
                anim.SetInteger("currentSkill", currentSkill);
                anim.SetBool("DEAD", state == "DEAD");
            }
        }

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "LateUpdate_");
    }

    void OnDestroy() {
        // Unity bug: isServer is false when called in host mode. only true when
        // called in dedicated mode. so we need a workaround:
        if (NetworkServer.active) { // isServer
            // leave party (if any)
            if (InParty()) {
                // dismiss if master, leave otherwise
                if (party.members[0] == name)
                    PartyDismiss();
                else
                    PartyLeave();
            }

            // notify guild members that we are offline
            SetGuildOnline(false);

            // destroy pet
            if (activePet != null)
                NetworkServer.Destroy(activePet.gameObject);
        }

        if (isLocalPlayer) { // requires at least Unity 5.5.1 bugfix to work
            Destroy(indicator);
            SaveSkillbar();
        }

        onlinePlayers.Remove(name);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnDestroy_");
    }

    // finite state machine events - status based //////////////////////////////
    // status based events
    bool EventDied() {
        return health == 0;
    }

    bool EventTargetDisappeared() {
        return target == null;
    }

    bool EventTargetDied() {
        return target != null && target.health == 0;
    }

    bool EventSkillRequest() {
        return 0 <= currentSkill && currentSkill < skills.Count;
    }

    bool EventSkillFinished() {
        return 0 <= currentSkill && currentSkill < skills.Count &&
               skills[currentSkill].CastTimeRemaining() == 0;
    }

    bool EventMoveEnd() {
        return state == "MOVING" && !IsMoving();
    }

    bool EventTradeStarted() {
        // did someone request a trade? and did we request a trade with him too?
        var player = FindPlayerFromTradeInvitation();
        return player != null && player.tradeRequestFrom == name;
    }

    bool EventTradeDone() {
        // trade canceled or finished?
        return state == "TRADING" && tradeRequestFrom == "";
    }

    // finite state machine events - command based /////////////////////////////
    // client calls command, command sets a flag, event reads and resets it
    // => we use a set so that we don't get ultra long queues etc.
    // => we use set.Return to read and clear values
    HashSet<string> cmdEvents = new HashSet<string>();

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdRespawn() { cmdEvents.Add("Respawn"); }
    bool EventRespawn() { return cmdEvents.Remove("Respawn"); }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdCancelAction() { cmdEvents.Add("CancelAction"); }
    bool EventCancelAction() { return cmdEvents.Remove("CancelAction"); }

    Vector3 navigatePosition = Vector3.zero;
    float navigateStop = 0;
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    void CmdNavigateTo(Vector3 position, float stoppingDistance) {
        navigatePosition = position; navigateStop = stoppingDistance;
        cmdEvents.Add("NavigateTo");
    }
    bool EventNavigateTo() { return cmdEvents.Remove("NavigateTo"); }

    // finite state machine - server ///////////////////////////////////////////
    [Server]
    string UpdateServer_IDLE() {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied()) {
            // we died.
            OnDeath();
            currentSkill = nextSkill = -1; // in case we died while trying to cast
            return "DEAD";
        }
        if (EventCancelAction()) {
            // the only thing that we can cancel is the target
            target = null;
            return "IDLE";
        }
        if (EventTradeStarted()) {
            // cancel casting (if any), set target, go to trading
            currentSkill = nextSkill = -1; // just in case
            target = FindPlayerFromTradeInvitation();
            return "TRADING";
        }
        if (EventNavigateTo()) {
            // cancel casting (if any) and start moving
            currentSkill = nextSkill = -1;
            // move
            agent.stoppingDistance = navigateStop;
            agent.destination = navigatePosition;
            return "MOVING";
        }
        if (EventSkillRequest()) {
            // user wants to cast a skill.
            // check self (alive, mana, weapon etc.) and target
            var skill = skills[currentSkill];
            nextTarget = target; // return to this one after any corrections by CastCheckTarget
            if (CastCheckSelf(skill) && CastCheckTarget(skill)) {
                // check distance between self and target
                if (CastCheckDistance(skill)) {
                    // start casting and set the casting end time
                    skill.castTimeEnd = Time.time + skill.castTime;
                    skills[currentSkill] = skill;
                    return "CASTING";
                } else {
                    // move to the target first
                    // (use collider point(s) to also work with big entities)
                    agent.stoppingDistance = skill.castRange;
                    agent.destination = target.collider.ClosestPointOnBounds(transform.position);
                    return "MOVING";
                }
            } else {
                // checks failed. stop trying to cast.
                currentSkill = nextSkill = -1;
                return "IDLE";
            }
        }
        if (EventSkillFinished()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "IDLE"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_MOVING() {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied()) {
            // we died.
            OnDeath();
            currentSkill = nextSkill = -1; // in case we died while trying to cast
            return "DEAD";
        }
        if (EventMoveEnd()) {
            // finished moving. do whatever we did before.
            return "IDLE";
        }
        if (EventCancelAction()) {
            // cancel casting (if any) and stop moving
            currentSkill = nextSkill = -1;
            agent.ResetPath();
            return "IDLE";
        }
        if (EventTradeStarted()) {
            // cancel casting (if any), stop moving, set target, go to trading
            currentSkill = nextSkill = -1;
            agent.ResetPath();
            target = FindPlayerFromTradeInvitation();
            return "TRADING";
        }
        if (EventNavigateTo()) {
            // cancel casting (if any) and start moving
            currentSkill = nextSkill = -1;
            agent.stoppingDistance = navigateStop;
            agent.destination = navigatePosition;
            return "MOVING";
        }
        if (EventSkillRequest()) {
            // if and where we keep moving depends on the skill and the target
            // check self (alive, mana, weapon etc.) and target
            var skill = skills[currentSkill];
            nextTarget = target; // return to this one after any corrections by CastCheckTarget
            if (CastCheckSelf(skill) && CastCheckTarget(skill)) {
                // check distance between self and target
                if (CastCheckDistance(skill)) {
                    // stop moving, start casting and set the casting end time
                    agent.ResetPath();
                    skill.castTimeEnd = Time.time + skill.castTime;
                    skills[currentSkill] = skill;
                    return "CASTING";
                } else {
                    // keep moving towards the target
                    // (use collider point(s) to also work with big entities)
                    agent.stoppingDistance = skill.castRange;
                    agent.destination = target.collider.ClosestPointOnBounds(transform.position);
                    return "MOVING";
                }
            } else {
                // invalid target. stop trying to cast, but keep moving.
                currentSkill = nextSkill = -1;
                return "MOVING";
            }
        }
        if (EventSkillFinished()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "MOVING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_CASTING() {
        // keep looking at the target for server & clients (only Y rotation)
        if (target) LookAtY(target.transform.position);

        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied()) {
            // we died.
            OnDeath();
            currentSkill = nextSkill = -1; // in case we died while trying to cast
            return "DEAD";
        }
        if (EventNavigateTo()) {
            // cancel casting and start moving
            currentSkill = nextSkill = -1;
            agent.stoppingDistance = navigateStop;
            agent.destination = navigatePosition;
            return "MOVING";
        }
        if (EventCancelAction()) {
            // cancel casting
            currentSkill = nextSkill = -1;
            return "IDLE";
        }
        if (EventTradeStarted()) {
            // cancel casting (if any), stop moving, set target, go to trading
            currentSkill = nextSkill = -1;
            agent.ResetPath();
            target = FindPlayerFromTradeInvitation();
            return "TRADING";
        }
        if (EventTargetDisappeared()) {
            // cancel if we were trying to cast an attack skill
            if (skills[currentSkill].category == "Attack") {
                currentSkill = nextSkill = -1;
                return "IDLE";
            }
        }
        if (EventTargetDied()) {
            // cancel if we were trying to cast an attack skill
            if (skills[currentSkill].category == "Attack") {
                currentSkill = nextSkill = -1;
                return "IDLE";
            }
        }
        if (EventSkillFinished()) {
            // apply the skill after casting is finished
            // note: we don't check the distance again. it's more fun if players
            //       still cast the skill if the target ran a few steps away
            var skill = skills[currentSkill];

            // apply the skill on the target
            CastSkill(skill);

            // casting finished for now. user pressed another skill button?
            if (nextSkill != -1) {
                currentSkill = nextSkill;
                nextSkill = -1;
            // skill should be followed with default attack? otherwise clear
            } else currentSkill = skill.followupDefaultAttack ? 0 : -1;

            // user tried to target something while casting? or we saved the
            // target before correcting it in CastCheckTarget?
            // (we have to wait until the skill is finished, otherwise people
            //  may start to cast and then switch to a far away target while
            //  casting, etc.)
            if (nextTarget != null) {
                target = nextTarget;
                nextTarget = null;
            }

            // go back to IDLE
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "CASTING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_TRADING() {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied()) {
            // we died, stop trading. other guy will receive targetdied event.
            OnDeath();
            currentSkill = nextSkill = -1; // in case we died while trying to cast
            TradeCleanup();
            return "DEAD";
        }
        if (EventCancelAction()) {
            // stop trading
            TradeCleanup();
            return "IDLE";
        }
        if (EventTargetDisappeared()) {
            // target disconnected, stop trading
            TradeCleanup();
            return "IDLE";
        }
        if (EventTargetDied()) {
            // target died, stop trading
            TradeCleanup();
            return "IDLE";
        }
        if (EventTradeDone()) {
            // someone canceled or we finished the trade. stop trading
            TradeCleanup();
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTradeStarted()) {} // don't care
        if (EventNavigateTo()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "TRADING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_DEAD() {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventRespawn()) {
            // revive to closest spawn, with 50% health, then go to idle
            var start = NetworkManager.singleton.GetNearestStartPosition(transform.position);
            agent.Warp(start.position); // recommended over transform.position
            Revive(0.5f);
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventDied()) {} // don't care
        if (EventCancelAction()) {} // don't care
        if (EventTradeStarted()) {} // don't care
        if (EventTradeDone()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventNavigateTo()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "DEAD"; // nothing interesting happened
    }

    [Server]
    protected override string UpdateServer() {
        if (state == "IDLE")    return UpdateServer_IDLE();
        if (state == "MOVING")  return UpdateServer_MOVING();
        if (state == "CASTING") return UpdateServer_CASTING();
        if (state == "TRADING") return UpdateServer_TRADING();
        if (state == "DEAD")    return UpdateServer_DEAD();
        Debug.LogError("invalid state:" + state);
        return "IDLE";
    }

    // finite state machine - client ///////////////////////////////////////////
    [Client]
    protected override void UpdateClient() {
        if (state == "IDLE" || state == "MOVING") {
            if (isLocalPlayer) {
                // simply accept input
                SelectionHandling();
                WSADHandling();
                TargetNearest();

                // canel action if escape key was pressed
                if (Input.GetKeyDown(KeyCode.Escape)) CmdCancelAction();
            }
        } else if (state == "CASTING") {
            // keep looking at the target for server & clients (only Y rotation)
            if (target) LookAtY(target.transform.position);

            if (isLocalPlayer) {
                // simply accept input
                SelectionHandling();
                WSADHandling();
                TargetNearest();

                // canel action if escape key was pressed
                if (Input.GetKeyDown(KeyCode.Escape)) CmdCancelAction();
            }
        } else if (state == "TRADING") {
        } else if (state == "DEAD") {
        } else Debug.LogError("invalid state:" + state);

        if (nameOverlay != null) {
            var player = Utils.ClientLocalPlayer();

            // note: murderer has higher priority (a player can be a murderer and an
            // offender at the same time)
            if (IsMurderer())
                nameOverlay.color = nameOverlayMurdererColor;
            else if (IsOffender())
                nameOverlay.color = nameOverlayOffenderColor;
            // member of the same party
            else if (player.InParty() && player.party.GetMemberIndex(name) != -1)
                nameOverlay.color = nameOverlayPartyColor;
            // otherwise default
            else
                nameOverlay.color = nameOverlayDefaultColor;
        }
        if (guildOverlay != null)
            guildOverlay.text = guildName != "" ? guildOverlayPrefix + guildName + guildOverlaySuffix : "";

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "UpdateClient_");
    }

    // attributes //////////////////////////////////////////////////////////////
    public int AttributesSpendable() {
        // calculate the amount of attribute points that can still be spent
        // -> one point per level
        // -> we don't need to store the points in an extra variable, we can
        //    simply decrease the attribute points spent from the level
        return level - (strength + intelligence);
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdIncreaseStrength() {
        // validate
        if (health > 0 && AttributesSpendable() > 0) ++strength;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdIncreaseIntelligence() {
        // validate
        if (health > 0 && AttributesSpendable() > 0) ++intelligence;
    }

    // combat //////////////////////////////////////////////////////////////////
    // helper function to calculate the experience rewards for sharing parties
    public static long CalculatePartyExperienceShare(long total, int memberCount, float bonusPercentagePerMember, int memberLevel, int killedLevel) {
        // bonus percentage based on how many members there are
        float bonusPercentage = (memberCount-1) * bonusPercentagePerMember;

        // calculate the share via ceil, so that uneven numbers still result in
        // at least 'total' in the end.
        // e.g. 4/2=2 (good); 5/2=2 (1 point got lost)
        long share = (long)Mathf.Ceil(total / (float)memberCount);

        // balance experience reward for the receiver's level. this is important
        // to avoid crazy power leveling where a level 1 hero would get a LOT of
        // level ups if his friend kills a level 100 monster once.
        long balanced = BalanceExpReward(share, memberLevel, killedLevel);
        long bonus = Convert.ToInt64(balanced * bonusPercentage);

        return balanced + bonus;
    }

    [Server]
    public void OnDamageDealtToMonster(Monster monster) {
        // did we kill it?
        if (monster.health == 0) {
            // share kill rewards with party or only for self
            var closeMembers = InParty() ? GetPartyMembersInProximity() : new List<Player>();

            // share experience & skill experience
            // note: bonus only applies to exp. share parties, otherwise
            //       there's an unnecessary pressure to always join a
            //       party when leveling alone too.
            // note: if monster.rewardExp is 10 then it's possible that
            //       two members only receive 2 exp each (= 4 total).
            //       this happens because of exp balancing by level and
            //       is as intended.
            if (InParty() && party.shareExperience) {
                foreach (var member in closeMembers) {
                    member.experience += CalculatePartyExperienceShare(
                        monster.rewardExperience,
                        closeMembers.Count,
                        Party.BonusExperiencePerMember,
                        member.level,
                        monster.level
                    );
                    member.skillExperience += CalculatePartyExperienceShare(
                        monster.rewardSkillExperience,
                        closeMembers.Count,
                        Party.BonusExperiencePerMember,
                        member.level,
                        monster.level
                    );
                }
            } else {
                skillExperience += BalanceExpReward(monster.rewardSkillExperience, level, monster.level);
                experience += BalanceExpReward(monster.rewardExperience, level, monster.level);
            }

            // give pet the same exp without dividing it, but balance it
            // => AFTER player exp reward! pet can only ever level up to player
            //    level, so it's best if the player gets exp and level-ups
            //    first, then afterwards we try to level up the pet.
            if (activePet != null)
                activePet.experience += BalanceExpReward(monster.rewardExperience, activePet.level, monster.level);

            // increase quest kill counter for all party members
            if (InParty()) {
                foreach (var member in closeMembers)
                    member.IncreaseQuestKillCounterFor(monster.name);
            } else IncreaseQuestKillCounterFor(monster.name);
        }
    }

    [Server]
    public void OnDamageDealtToPlayer(Player player) {
        // was he innocent?
        if (!player.IsOffender() && !player.IsMurderer()) {
            // did we kill him? then start/reset murder status
            // did we just attack him? then start/reset offender status
            // (unless we are already a murderer)
            if (player.health == 0) StartMurderer();
            else if (!IsMurderer()) StartOffender();
        }
    }

    [Server]
    public void OnDamageDealtToPet(Pet pet) {
        // was he innocent?
        if (!pet.owner.IsOffender() && !pet.owner.IsMurderer()) {
            // did we kill him? then start/reset murder status
            // did we just attack him? then start/reset offender status
            // (unless we are already a murderer)
            if (pet.health == 0) StartMurderer();
            else if (!IsMurderer()) StartOffender();
        }
    }

    // custom DealDamageAt function that also rewards experience if we killed
    // the monster
    [Server]
    public override HashSet<Entity> DealDamageAt(Entity entity, int amount, float aoeRadius=0) {
        // deal damage with the default function. get all entities that were hit
        // in the AoE radius
        var entities = base.DealDamageAt(entity, amount, aoeRadius);
        foreach (var e in entities) {
            // a monster?
            if (e is Monster) {
                OnDamageDealtToMonster((Monster)e);
            // a player?
            // (see murder code section comments to understand the system)
            } else if (e is Player) {
                OnDamageDealtToPlayer((Player)e);
            // a pet?
            // (see murder code section comments to understand the system)
            } else if (e is Pet) {
                OnDamageDealtToPet((Pet)e);
            }
        }

        // let pet know that we attacked something
        if (activePet != null && activePet.autoAttack)
            activePet.OnAggro(entity);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "DealDamageAt_", entities, amount);

        return entities; // not really needed anywhere
    }

    // experience //////////////////////////////////////////////////////////////
    public float ExperiencePercent() {
        return (experience != 0 && experienceMax != 0) ? (float)experience / (float)experienceMax : 0;
    }

    // players gain exp depending on their level. if a player has a lower level
    // than the monster, then he gains more exp (up to 100% more) and if he has
    // a higher level, then he gains less exp (up to 100% less)
    // -> test with monster level 20 and expreward of 100:
    //   BalanceExpReward( 1, 20, 100)); => 200
    //   BalanceExpReward( 9, 20, 100)); => 200
    //   BalanceExpReward(10, 20, 100)); => 200
    //   BalanceExpReward(11, 20, 100)); => 190
    //   BalanceExpReward(12, 20, 100)); => 180
    //   BalanceExpReward(13, 20, 100)); => 170
    //   BalanceExpReward(14, 20, 100)); => 160
    //   BalanceExpReward(15, 20, 100)); => 150
    //   BalanceExpReward(16, 20, 100)); => 140
    //   BalanceExpReward(17, 20, 100)); => 130
    //   BalanceExpReward(18, 20, 100)); => 120
    //   BalanceExpReward(19, 20, 100)); => 110
    //   BalanceExpReward(20, 20, 100)); => 100
    //   BalanceExpReward(21, 20, 100)); =>  90
    //   BalanceExpReward(22, 20, 100)); =>  80
    //   BalanceExpReward(23, 20, 100)); =>  70
    //   BalanceExpReward(24, 20, 100)); =>  60
    //   BalanceExpReward(25, 20, 100)); =>  50
    //   BalanceExpReward(26, 20, 100)); =>  40
    //   BalanceExpReward(27, 20, 100)); =>  30
    //   BalanceExpReward(28, 20, 100)); =>  20
    //   BalanceExpReward(29, 20, 100)); =>  10
    //   BalanceExpReward(30, 20, 100)); =>   0
    //   BalanceExpReward(31, 20, 100)); =>   0
    public static long BalanceExpReward(long reward, int attackerLevel, int victimLevel) {
        int levelDiff = Mathf.Clamp(victimLevel - attackerLevel, -10, 10);
        float multiplier = 1 + levelDiff * 0.1f;
        return Convert.ToInt64(reward * multiplier);
    }

    // aggro ///////////////////////////////////////////////////////////////////
    // this function is called by entities that attack us
    [ServerCallback]
    public override void OnAggro(Entity entity) {
        // forward to pet if it's supposed to defend us
        if (activePet != null && activePet.defendOwner)
            activePet.OnAggro(entity);
    }

    // death ///////////////////////////////////////////////////////////////////
    [Server]
    void OnDeath() {
        // stop any movement and buffs, clear target
        agent.ResetPath();
        StopBuffs();
        target = null;

        // lose experience
        long loss = Convert.ToInt64(experienceMax * deathExperienceLossPercent);
        experience -= loss;

        // send an info chat message
        string message = "You died and lost " + loss + " experience.";
        chat.TargetMsgInfo(connectionToClient, message);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnDeath_");
    }

    // loot ////////////////////////////////////////////////////////////////////
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTakeLootGold() {
        // validate: dead monster and close enough?
        // use collider point(s) to also work with big entities
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            target != null && target is Monster && target.health == 0 &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange)
        {
            // distribute reward through party or to self
            if (InParty() && party.shareGold) {
                // find all party members in observer range
                // (we don't distribute it all across the map. standing
                //  next to each other is a better experience. players
                //  can't just stand safely in a city while gaining exp)
                var closeMembers = GetPartyMembersInProximity();

                // calculate the share via ceil, so that uneven numbers
                // still result in at least total gold in the end.
                // e.g. 4/2=2 (good); 5/2=2 (1 gold got lost)
                long share = (long)Mathf.Ceil((float)target.gold / (float)closeMembers.Count);

                // now distribute
                foreach (var member in closeMembers)
                    member.gold += share;
            } else {
                gold += target.gold;
            }

            // reset target gold
            target.gold = 0;
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTakeLootItem(int index) {
        // validate: dead monster and close enough and valid loot index?
        // use collider point(s) to also work with big entities
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            target != null && target is Monster && target.health == 0 &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            0 <= index && index < target.inventory.Count &&
            target.inventory[index].valid)
        {
            var item = target.inventory[index];

            // try to add it to the inventory, clear monster slot if it worked
            if (InventoryAddAmount(item.template, item.amount)) {
                item.valid = false;
                target.inventory[index] = item;
            }
        }
    }

    // inventory ///////////////////////////////////////////////////////////////
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdSwapInventoryTrash(int inventoryIndex) {
        // dragging an inventory item to the trash always overwrites the trash
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= inventoryIndex && inventoryIndex < inventory.Count) {
            // inventory slot has to be valid and destroyable and not summoned
            if (inventory[inventoryIndex].valid &&
                inventory[inventoryIndex].destroyable &&
                !inventory[inventoryIndex].petSummoned) {
                // overwrite trash
                trash = inventory[inventoryIndex];
                // clear inventory slot
                var temp = inventory[inventoryIndex];
                temp.valid = false;
                inventory[inventoryIndex] = temp;
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdSwapTrashInventory(int inventoryIndex) {
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= inventoryIndex && inventoryIndex < inventory.Count) {
            // inventory slot has to be empty or destroyable
            if (!inventory[inventoryIndex].valid || inventory[inventoryIndex].destroyable) {
                // swap them
                var temp = inventory[inventoryIndex];
                inventory[inventoryIndex] = trash;
                trash = temp;
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdSwapInventoryInventory(int fromIndex, int toIndex) {
        // note: should never send a command with complex types!
        // validate: make sure that the slots actually exist in the inventory
        // and that they are not equal
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= fromIndex && fromIndex < inventory.Count &&
            0 <= toIndex && toIndex < inventory.Count &&
            fromIndex != toIndex) {
            // swap them
            var temp = inventory[fromIndex];
            inventory[fromIndex] = inventory[toIndex];
            inventory[toIndex] = temp;
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdInventorySplit(int fromIndex, int toIndex) {
        // note: should never send a command with complex types!
        // validate: make sure that the slots actually exist in the inventory
        // and that they are not equal
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= fromIndex && fromIndex < inventory.Count &&
            0 <= toIndex && toIndex < inventory.Count &&
            fromIndex != toIndex) {
            // slotFrom has to have an entry, slotTo has to be empty
            if (inventory[fromIndex].valid && !inventory[toIndex].valid) {
                // from entry needs at least amount of 2
                if (inventory[fromIndex].amount >= 2) {
                    // split them serversided (has to work for even and odd)
                    var itemFrom = inventory[fromIndex];
                    var itemTo = inventory[fromIndex]; // copy the value
                    //inventory[toIndex] = inventory[fromIndex]; // copy value type
                    itemTo.amount = itemFrom.amount / 2;
                    itemFrom.amount -= itemTo.amount; // works for odd too

                    // put back into the list
                    inventory[fromIndex] = itemFrom;
                    inventory[toIndex] = itemTo;
                }
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdInventoryMerge(int fromIndex, int toIndex) {
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= fromIndex && fromIndex < inventory.Count &&
            0 <= toIndex && toIndex < inventory.Count &&
            fromIndex != toIndex) {
            // both items have to be valid
            if (inventory[fromIndex].valid && inventory[toIndex].valid) {
                // make sure that items are the same type
                if (inventory[fromIndex].name == inventory[toIndex].name) {
                    // merge from -> to
                    var itemFrom = inventory[fromIndex];
                    var itemTo = inventory[toIndex];
                    int stack = Mathf.Min(itemFrom.amount + itemTo.amount, itemTo.maxStack);
                    int put = stack - itemFrom.amount;
                    itemFrom.amount = itemTo.amount - put;
                    itemTo.amount = stack;
                    // 'from' empty now? then clear it
                    if (itemFrom.amount == 0) itemFrom.valid = false;
                    // put back into the list
                    inventory[fromIndex] = itemFrom;
                    inventory[toIndex] = itemTo;
                }
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdUseInventoryItem(int index) {
        // validate
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= index && index < inventory.Count && inventory[index].valid &&
            level >= inventory[index].minLevel) {
            // what we have to do depends on the category
            var item = inventory[index];
            if (item.category.StartsWith("Potion")) {
                // use
                health += item.usageHealth;
                mana += item.usageMana;
                experience += item.usageExperience;
                if (activePet != null) activePet.health += item.usagePetHealth;

                // decrease amount or destroy
                if (item.usageDestroy) {
                    --item.amount;
                    if (item.amount == 0) item.valid = false;
                    inventory[index] = item; // put new values in there
                }
            } else if (item.category.StartsWith("Equipment")) {
                // for each slot: find out if equipable and then do so
                for (int i = 0; i < equipment.Count; ++i)
                    if (CanEquip(equipmentInfo[i].requiredCategory, item))
                        SwapInventoryEquip(index, i);
            } else if (item.category.StartsWith("Pet")) {
                // does the item have a summon prefab?
                if (item.petPrefab != null) {
                    // summon only if:
                    //  no other is summoned yet
                    //  pet not dead (dead pet item has to be revived first)
                    //  player level at least pet level to avoid power leveling
                    //    with someone else's high level pet
                    if (activePet == null && item.petHealth > 0 && item.petLevel <= level) {
                        // summon right next to the player
                        GameObject go = Instantiate(item.petPrefab.gameObject, petDestination, Quaternion.identity);
                        Pet pet = go.GetComponent<Pet>();
                        pet.name = item.petPrefab.name; // avoid "(Clone)"
                        pet.owner = this;
                        pet.health = item.petHealth;
                        pet.level = item.petLevel;
                        pet.experience = item.petExperience;

                        NetworkServer.Spawn(go);
                        activePet = go.GetComponent<Pet>(); // set syncvar to go after spawning

                        // set item summoned pet reference so we know it can't be sold etc.
                        item.petSummoned = go;
                        inventory[index] = item;
                    // unsummon: only if this one was summoned
                    } else if (item.petSummoned != null) {
                        // only if idle to avoid unsummon before taking damage
                        if (state == "IDLE" && activePet.state == "IDLE") {
                            // destroy from world. item.summoned and activePet
                            // will be null automatically.
                            NetworkServer.Destroy(activePet.gameObject);
                        } else {
                            string message = "Can't unsummon a Pet while fighting.";
                            chat.TargetMsgInfo(connectionToClient, message);
                        }
                    }
                }
            }

            // addon system hooks
            Utils.InvokeMany(typeof(Player), this, "OnUseInventoryItem_", index);
        }
    }

    // equipment ///////////////////////////////////////////////////////////////
    public int GetEquipmentIndexByName(string itemName) {
        return equipment.FindIndex(item => item.valid && item.name == itemName);
    }

    [Server]
    public bool CanEquip(string requiredCategory, Item item) {
        // note: we use StartsWith because a sword could also have the type
        //       EquipmentWeaponSpecial or whatever, which is fine too
        // note: empty slot types shouldn't be able to equip anything
        return requiredCategory != "" &&
               item.category.StartsWith(requiredCategory) &&
               level >= item.minLevel;
    }

    void OnEquipmentChanged(SyncListItem.Operation op, int index) {
        // update the model
        RefreshLocation(index);
    }

    void RebindAnimators() {
        foreach (var anim in GetComponentsInChildren<Animator>())
            anim.Rebind();
    }

    void RefreshLocation(int index) {
        Item item = equipment[index];
        EquipmentInfo info = equipmentInfo[index];

        // valid category and valid location? otherwise don't bother
        if (info.requiredCategory != "" && info.location != null) {
            // clear previous one in any case (when overwriting or clearing)
            if (info.location.childCount > 0) Destroy(info.location.GetChild(0).gameObject);

            //  valid item? and has a model? then set it
            if (item.valid && item.modelPrefab != null) {
                // load the model
                var go = Instantiate(item.modelPrefab);
                go.transform.SetParent(info.location, false);

                // is it a skinned mesh with an animator?
                var anim = go.GetComponent<Animator>();
                if (anim != null) {
                    // assign main animation controller to it
                    anim.runtimeAnimatorController = animator.runtimeAnimatorController;

                    // restart all animators, so that skinned mesh equipment will be
                    // in sync with the main animation
                    RebindAnimators();
                }
            }
        }
    }

    public void SwapInventoryEquip(int inventoryIndex, int equipIndex) {
        // validate: make sure that the slots actually exist in the inventory
        // and that they are not equal
        if (health > 0 &&
            0 <= inventoryIndex && inventoryIndex < inventory.Count &&
            0 <= equipIndex && equipIndex < equipment.Count) {
            // slotInv has to be empty or equipable
            if (!inventory[inventoryIndex].valid || CanEquip(equipmentInfo[equipIndex].requiredCategory, inventory[inventoryIndex])) {
                // swap them
                var temp = equipment[equipIndex];
                equipment[equipIndex] = inventory[inventoryIndex];
                inventory[inventoryIndex] = temp;
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdSwapInventoryEquip(int inventoryIndex, int equipIndex) {
        // SwapInventoryEquip sometimes needs to be called by the server and
        // sometimes as a Command by clients, but calling a Command from the
        // Server causes a UNET error, so we need it once as a normal function
        // and once as a Command.
        SwapInventoryEquip(inventoryIndex, equipIndex);
    }

    // skills //////////////////////////////////////////////////////////////////
    public override bool HasCastWeapon() {
        // equipped any 'EquipmentWeapon...' item?
        return equipment.FindIndex(item => item.valid && item.category.StartsWith("EquipmentWeapon")) != -1;
    }

    public override bool CanAttack(Entity entity) {
        return health > 0 &&
               entity.health > 0 &&
               entity != this &&
               (entity.GetType() == typeof(Monster) ||
                entity.GetType() == typeof(Player) ||
                (entity.GetType() == typeof(Pet) && entity != activePet));
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdUseSkill(int skillIndex) {
        // validate
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= skillIndex && skillIndex < skills.Count) {
            // can the skill be casted?
            if (skills[skillIndex].learned && skills[skillIndex].IsReady()) {
                // add as current or next skill, unless casting same one already
                // (some players might hammer the key multiple times, which
                //  doesn't mean that they want to cast it afterwards again)
                // => also: always set currentSkill when moving or idle or whatever
                //  so that the last skill that the player tried to cast while
                //  moving is the first skill that will be casted when attacking
                //  the enemy.
                if (currentSkill == -1 || state != "CASTING")
                    currentSkill = skillIndex;
                else if (currentSkill != skillIndex)
                    nextSkill = skillIndex;
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdLearnSkill(int skillIndex) {
        // validate
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= skillIndex && skillIndex < skills.Count) {
            var skill = skills[skillIndex];

            // not learned already? enough skill exp, required level?
            // note: status effects aren't learnable
            if (!skill.category.StartsWith("Status") &&
                !skill.learned &&
                level >= skill.requiredLevel &&
                skillExperience >= skill.requiredSkillExperience) {
                // decrease skill experience
                skillExperience -= skill.requiredSkillExperience;

                // learn skill
                skill.learned = true;
                skills[skillIndex] = skill;
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdUpgradeSkill(int skillIndex) {
        // validate
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= skillIndex && skillIndex < skills.Count) {
            var skill = skills[skillIndex];

            // already learned? enough skill exp and required level for upgrade?
            // and can be upgraded?
            // note: status effects aren't upgradeable
            if (!skill.category.StartsWith("Status") &&
                skill.learned &&
                skill.level < skill.maxLevel &&
                level >= skill.upgradeRequiredLevel &&
                skillExperience >= skill.upgradeRequiredSkillExperience) {
                // decrease skill experience
                skillExperience -= skill.upgradeRequiredSkillExperience;

                // upgrade
                ++skill.level;
                skills[skillIndex] = skill;
            }
        }
    }

    // skillbar ////////////////////////////////////////////////////////////////
    //[Client] <- disabled while UNET OnDestroy isLocalPlayer bug exists
    void SaveSkillbar() {
        // save skillbar to player prefs (based on player name, so that
        // each character can have a different skillbar)
        for (int i = 0; i < skillbar.Length; ++i)
            PlayerPrefs.SetString(name + "_skillbar_" + i, skillbar[i].reference);

        // force saving playerprefs, otherwise they aren't saved for some reason
        PlayerPrefs.Save();
    }

    [Client]
    void LoadSkillbar() {
        print("loading skillbar for " + name);
        var learned = skills.Where(skill => skill.learned).ToList();
        for (int i = 0; i < skillbar.Length; ++i) {
            // try loading an existing entry. otherwise fill with default skills
            // for a better first impression
            if (PlayerPrefs.HasKey(name + "_skillbar_" + i))
                skillbar[i].reference = PlayerPrefs.GetString(name + "_skillbar_" + i, "");
            else if (i < learned.Count)
                skillbar[i].reference = learned[i].name;
        }
    }

    // quests //////////////////////////////////////////////////////////////////
    public int GetQuestIndexByName(string questName) {
        return quests.FindIndex(quest => quest.name == questName);
    }

    [Server]
    public void IncreaseQuestKillCounterFor(string monsterName) {
        for (int i = 0; i < quests.Count; ++i) {
            // active quest and not completed yet?
            if (!quests[i].completed && quests[i].killName == monsterName) {
                var quest = quests[i];
                quest.killed = Mathf.Min(quest.killed + 1, quest.killAmount);
                quests[i] = quest;
            }
        }
    }

    // helper function to check if the player has completed a quest before
    public bool HasCompletedQuest(string questName) {
        return quests.Any(q => q.name == questName && q.completed);
    }

    // helper function to check if a player has an active (not completed) quest
    public bool HasActiveQuest(string questName) {
        return quests.Any(q => q.name == questName && !q.completed);
    }

    // helper function to check if the player can accept a new quest
    // note: no quest.completed check needed because we have a'not accepted yet'
    //       check
    public bool CanStartQuest(QuestTemplate quest) {
        // not too many quests yet?
        // has required level?
        // not accepted yet?
        // has finished predecessor quest (if any)?
        return quests.Count < questLimit &&
               level >= quest.requiredLevel &&          // has required level?
               GetQuestIndexByName(quest.name) == -1 && // not accepted yet?
               (quest.predecessor == null || HasCompletedQuest(quest.predecessor.name));
    }

    // helper function to check if the player can complete a quest
    public bool CanCompleteQuest(string questName) {
        // has the quest and not completed yet?
        int index = GetQuestIndexByName(questName);
        if (index != -1 && !quests[index].completed) {
            // fulfilled?
            var quest = quests[index];
            int gathered = quest.gatherName != "" ? InventoryCountAmount(quest.gatherName) : 0;
            if (quest.IsFulfilled(gathered)) return true;
        }
        return false;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdAcceptQuest(int npcQuestIndex) {
        // validate
        // use collider point(s) to also work with big entities
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            0 <= npcQuestIndex && npcQuestIndex < ((Npc)target).quests.Length &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            CanStartQuest(((Npc)target).quests[npcQuestIndex]))
        {
            var npcQuest = ((Npc)target).quests[npcQuestIndex];
            quests.Add(new Quest(npcQuest));
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdCompleteQuest(int npcQuestIndex) {
        // validate
        // use collider point(s) to also work with big entities
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            0 <= npcQuestIndex && npcQuestIndex < ((Npc)target).quests.Length &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange)
        {
            var npc = (Npc)target;
            var npcQuest = npc.quests[npcQuestIndex];

            // does the player have that quest?
            int index = GetQuestIndexByName(npcQuest.name);
            if (index != -1) {
                var quest = quests[index];

                // not completed before?
                if (!quest.completed) {
                    // is it fulfilled (are all requirements met)?
                    int gathered = quest.gatherName != "" ? InventoryCountAmount(quest.gatherName) : 0;
                    if (quest.IsFulfilled(gathered)) {
                        // no reward item, or if there is one: enough space?
                        if (quest.rewardItem == null ||
                            InventoryCanAddAmount(quest.rewardItem, 1)) {
                            // remove gathered items from player's inventory
                            if (quest.gatherName != "")
                                InventoryRemoveAmount(quest.gatherName, quest.gatherAmount);

                            // gain rewards
                            gold += quest.rewardGold;
                            experience += quest.rewardExperience;
                            if (quest.rewardItem != null)
                                InventoryAddAmount(quest.rewardItem, 1);

                            // complete quest
                            quest.completed = true;
                            quests[index] = quest;
                        }
                    }
                }
            }
        }
    }

    // npc trading /////////////////////////////////////////////////////////////
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdNpcBuyItem(int index, int amount) {
        // validate: close enough, npc alive and valid index?
        // use collider point(s) to also work with big entities
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            0 <= index && index < ((Npc)target).saleItems.Length)
        {
            var npcItem = ((Npc)target).saleItems[index];

            // valid amount?
            if (1 <= amount && amount <= npcItem.maxStack) {
                long price = npcItem.buyPrice * amount;

                // enough gold and enough space in inventory?
                if (gold >= price && InventoryCanAddAmount(npcItem, amount)) {
                    // pay for it, add to inventory
                    gold -= price;
                    InventoryAddAmount(npcItem, amount);
                }
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdNpcSellItem(int index, int amount) {
        // validate: close enough, npc alive and valid index and valid item?
        // use collider point(s) to also work with big entities
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            0 <= index && index < inventory.Count &&
            inventory[index].valid &&
            inventory[index].sellable &&
            !inventory[index].petSummoned)
        {
            var item = inventory[index];

            // valid amount?
            if (1 <= amount && amount <= item.amount) {
                // sell the amount
                long price = item.sellPrice * amount;
                gold += price;
                item.amount -= amount;
                if (item.amount == 0) item.valid = false;
                inventory[index] = item;
            }
        }
    }

    // npc teleport ////////////////////////////////////////////////////////////
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdNpcTeleport() {
        // validate
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            ((Npc)target).teleportTo != null)
        {
            // using agent.Warp is recommended over transform.position
            // (the latter can cause weird bugs when using it with an agent)
            agent.Warp(((Npc)target).teleportTo.position);
        }
    }

    // player to player trading ////////////////////////////////////////////////
    // how trading works:
    // 1. A invites his target with CmdTradeRequest()
    //    -> sets B.tradeInvitationFrom = A;
    // 2. B sees a UI window and accepts (= invites A too)
    //    -> sets A.tradeInvitationFrom = B;
    // 3. the TradeStart event is fired, both go to 'TRADING' state
    // 4. they lock the trades
    // 5. they accept, then items and gold are swapped

    public bool CanStartTrade() {
        // a player can only trade if he is not trading already and alive
        return health > 0 && state != "TRADING";
    }

    public bool CanStartTradeWith(Entity entity) {
        // can we trade? can the target trade? are we close enough?
        return entity != null && entity is Player && entity != this &&
               CanStartTrade() && ((Player)entity).CanStartTrade() &&
               Utils.ClosestDistance(collider, entity.collider) <= interactionRange;
    }

    // request a trade with the target player.
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeRequestSend() {
        // validate
        if (CanStartTradeWith(target)) {
            // send a trade request to target
            ((Player)target).tradeRequestFrom = name;
            print(name + " invited " + target.name + " to trade");
        }
    }

    // helper function to find the guy who sent us a trade invitation
    [Server]
    Player FindPlayerFromTradeInvitation() {
        if (tradeRequestFrom != "" && onlinePlayers.ContainsKey(tradeRequestFrom))
            return onlinePlayers[tradeRequestFrom];
        return null;
    }

    // accept a trade invitation by simply setting 'requestFrom' for the other
    // person to self
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeRequestAccept() {
        var sender = FindPlayerFromTradeInvitation();
        if (sender != null) {
            if (CanStartTradeWith(sender)) {
                // also send a trade request to the person that invited us
                sender.tradeRequestFrom = name;
                print(name + " accepted " + sender.name + "'s trade request");
            }
        }
    }

    // decline a trade invitation
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeRequestDecline() {
        tradeRequestFrom = "";
    }

    [Server]
    void TradeCleanup() {
        // clear all trade related properties
        tradeOfferGold = 0;
        for (int i = 0; i < tradeOfferItems.Count; ++i) tradeOfferItems[i] = -1;
        tradeStatus = TradeStatus.Free;
        tradeRequestFrom = "";
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeCancel() {
        // validate
        if (state == "TRADING") {
            // clear trade request for both guys. the FSM event will do the rest
            var player = FindPlayerFromTradeInvitation();
            if (player != null) player.tradeRequestFrom = "";
            tradeRequestFrom = "";
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeOfferLock() {
        // validate
        if (state == "TRADING")
            tradeStatus = TradeStatus.Locked;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeOfferGold(long amount) {
        // validate
        if (state == "TRADING" && tradeStatus == TradeStatus.Free &&
            0 <= amount && amount <= gold)
            tradeOfferGold = amount;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeOfferItem(int inventoryIndex, int offerIndex) {
        // validate
        if (state == "TRADING" && tradeStatus == TradeStatus.Free &&
            0 <= inventoryIndex && inventoryIndex < inventory.Count &&
            inventory[inventoryIndex].valid &&
            inventory[inventoryIndex].tradable &&
            !inventory[inventoryIndex].petSummoned &&
            0 <= offerIndex && offerIndex < tradeOfferItems.Count &&
            !tradeOfferItems.Contains(inventoryIndex)) // only one reference
            tradeOfferItems[offerIndex] = inventoryIndex;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeOfferItemClear(int offerIndex) {
        // validate
        if (state == "TRADING" && tradeStatus == TradeStatus.Free &&
            0 <= offerIndex && offerIndex < tradeOfferItems.Count)
            tradeOfferItems[offerIndex] = -1;
    }

    [Server]
    bool IsTradeOfferStillValid() {
        // enough gold and all offered items are -1 or valid?
        return gold >= tradeOfferGold &&
               tradeOfferItems.All(idx => idx == -1 ||
                                          (0 <= idx && idx < inventory.Count && inventory[idx].valid));
    }

    [Server]
    int TradeOfferItemSlotAmount() {
        return tradeOfferItems.Count(i => i != -1);
    }

    [Server]
    int InventorySlotsNeededForTrade() {
        // if other guy offers 2 items and we offer 1 item then we only need
        // 2-1 = 1 slots. and the other guy would need 1-2 slots and at least 0.
        if (target != null && target is Player) {
            var other = (Player)target;
            var otherAmount = other.TradeOfferItemSlotAmount();
            var myAmount = TradeOfferItemSlotAmount();
            return Mathf.Max(otherAmount - myAmount, 0);
        }
        return 0;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTradeOfferAccept() {
        // validate
        // note: distance check already done when starting the trade
        if (state == "TRADING" && tradeStatus == TradeStatus.Locked &&
            target != null && target is Player) {
            var other = (Player)target;

            // other has locked?
            if (other.tradeStatus == TradeStatus.Locked) {
                //  simply accept and wait for the other guy to accept too
                tradeStatus = TradeStatus.Accepted;
                print("first accept by " + name);
            // other has accepted already? then both accepted now, start trade.
            } else if (other.tradeStatus == TradeStatus.Accepted) {
                // accept
                tradeStatus = TradeStatus.Accepted;
                print("second accept by " + name);

                // both offers still valid?
                if (IsTradeOfferStillValid() && other.IsTradeOfferStillValid()) {
                    // both have enough inventory slots?
                    // note: we don't use InventoryCanAdd here because:
                    // - current solution works if both have full inventories
                    // - InventoryCanAdd only checks one slot. here we have
                    //   multiple slots though (it could happen that we can
                    //   not add slot 2 after we did add slot 1's items etc)
                    if (InventorySlotsFree() >= InventorySlotsNeededForTrade() &&
                        other.InventorySlotsFree() >= other.InventorySlotsNeededForTrade()) {
                        // exchange the items by first taking them out
                        // into a temporary list and then putting them
                        // in. this guarantees that exchanging even
                        // works with full inventories

                        // take them out
                        var tempMy = new Queue<Item>();
                        foreach (int index in tradeOfferItems) {
                            if (index != -1) {
                                tempMy.Enqueue(inventory[index]);
                                var item = inventory[index];
                                item.valid = false;
                                inventory[index] = item;
                            }
                        }

                        var tempOther = new Queue<Item>();
                        foreach (int index in other.tradeOfferItems) {
                            if (index != -1) {
                                tempOther.Enqueue(other.inventory[index]);
                                var item = other.inventory[index];
                                item.valid = false;
                                other.inventory[index] = item;
                            }
                        }

                        // put them into the free slots
                        for (int i = 0; i < inventory.Count; ++i)
                            if (!inventory[i].valid && tempOther.Count > 0)
                                inventory[i] = tempOther.Dequeue();

                        for (int i = 0; i < other.inventory.Count; ++i)
                            if (!other.inventory[i].valid && tempMy.Count > 0)
                                other.inventory[i] = tempMy.Dequeue();

                        // did it all work?
                        if (tempMy.Count > 0 || tempOther.Count > 0)
                            Debug.LogWarning("item trade problem");

                        // exchange the gold
                        gold -= tradeOfferGold;
                        other.gold -= other.tradeOfferGold;

                        gold += other.tradeOfferGold;
                        other.gold += tradeOfferGold;
                    }
                } else print("trade canceled (invalid offer)");

                // clear trade request for both guys. the FSM event will do the
                // rest
                tradeRequestFrom = "";
                other.tradeRequestFrom = "";
            }
        }
    }

    // crafting ////////////////////////////////////////////////////////////////
    // the crafting system is designed to work with all kinds of commonly known
    // crafting options:
    // - item combinations: wood + stone = axe
    // - weapon upgrading: axe + gem = strong axe
    // - recipe items: axerecipe(item) + wood(item) + stone(item) = axe(item)
    //
    // players can craft at all times, not just at npcs, because that's the most
    // realistic option

    // craft the current combination of items and put result into inventory
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdCraft(int[] indices) {
        // validate: between 1 and 6, all valid, no duplicates?
        if ((state == "IDLE" || state == "MOVING") &&
            0 < indices.Length && indices.Length <= RecipeTemplate.recipeSize &&
            indices.All(idx => 0 <= idx && idx < inventory.Count && inventory[idx].valid) &&
            !indices.ToList().HasDuplicates()) {

            // build list of item templates from indices
            var items = indices.Select(idx => inventory[idx].template).ToList();

            // find recipe
            var recipe = RecipeTemplate.dict.Values.ToList().Find(r => r.CanCraftWith(items)); // good enough for now
            if (recipe != null && recipe.result != null) {
                // try to add result item to inventory (if enough space)
                if (InventoryAddAmount(recipe.result, 1)) {
                    // it worked. remove the ingredients from inventory
                    foreach (int index in indices) {
                        // decrease item amount
                        var item = inventory[index];
                        --item.amount;
                        if (item.amount == 0) item.valid = false;
                        inventory[index] = item;
                    }
                }
            }
        }
    }

    // pvp murder system ///////////////////////////////////////////////////////
    // attacking someone innocent results in Offender status
    //   (can be attacked without penalty for a short time)
    // killing someone innocent results in Murderer status
    //   (can be attacked without penalty for a long time + negative buffs)
    // attacking/killing a Offender/Murderer has no penalty
    //
    // we use buffs for the offender/status because buffs have all the features
    // that we need here.
    public bool IsOffender() {
        return skills.Any(s => s.category == "StatusOffender" && s.BuffTimeRemaining() > 0);
    }

    public bool IsMurderer() {
        return skills.Any(s => s.category == "StatusMurderer" && s.BuffTimeRemaining() > 0);
    }

    public void StartOffender() {
        // start or reset the murderer buff
        for (int i = 0; i < skills.Count; ++i)
            if (skills[i].category == "StatusOffender") {
                var skill = skills[i];
                skill.buffTimeEnd = Time.time + skill.buffTime;
                skills[i] = skill;
            }
    }

    public void StartMurderer() {
        // start or reset the murderer buff
        for (int i = 0; i < skills.Count; ++i)
            if (skills[i].category == "StatusMurderer") {
                var skill = skills[i];
                skill.buffTimeEnd = Time.time + skill.buffTime;
                skills[i] = skill;
            }
    }

    // item mall ///////////////////////////////////////////////////////////////
    [Command]
    public void CmdEnterCoupon(string coupon) {
        // only allow entering one coupon every few seconds to avoid brute force
        if (Time.time >= nextRiskyActionTime) {
            // YOUR COUPON VALIDATION CODE HERE
            // coins += ParseCoupon(coupon);
            Debug.Log("coupon: " + coupon + " => " + name + "@" + Time.time);
            nextRiskyActionTime = Time.time + couponWaitSeconds;
        }
    }

    [Command]
    public void CmdUnlockItem(string itemName) {
        // note: passing a item index would use less bandwidth, but a string
        // allows us to find the item in the dictionary more easily and saves
        // computations. this function is called very rarely anyway.

        // validate: only if alive so people can't buy resurrection potions
        // after dieing in a PvP fight etc.
        if (health > 0 && ItemTemplate.dict.ContainsKey(itemName)) {
            var item = ItemTemplate.dict[itemName];
            if (0 < item.itemMallPrice && item.itemMallPrice <= coins) {
                // try to add it to the inventory, subtract costs from coins
                if (InventoryAddAmount(item, 1)) {
                    coins -= item.itemMallPrice;
                    Debug.Log(name + " unlocked " + itemName);
                }
            }
        }
    }

    // coins can't be increased by an external application while the player is
    // ingame. we use an additional table to store new orders in and process
    // them every few seconds from here. this way we can even notify the player
    // after his order was processed successfully.
    //
    // note: the alternative is to keep player.coins in the database at all
    // times, but then we need RPCs and the client needs a .coins value anyway.
    [Server]
    void ProcessCoinOrders() {
        var orders = Database.GrabCharacterOrders(name);
        foreach (long reward in orders) {
            coins += reward;
            Debug.Log("Processed order for: " + name + ";" + reward);
            string message = "Processed order for: " + reward;
            chat.TargetMsgInfo(connectionToClient, message);
        }
    }

    // guild ///////////////////////////////////////////////////////////////////
    public bool InGuild() {
        // only if both are true, otherwise we might be in the middle of leaving
        return guildName != "" && guild.GetMemberIndex(name) != -1;
    }

    [Server]
    static void BroadcastGuildChanges(string guildName, Guild guild) {
        // save in database
        Database.SaveGuild(guildName, guild.notice, guild.members.ToList());

        // copy to every online member. we don't just reload from db because the
        // online status is only available in the members list
        foreach (var member in guild.members) {
            if (onlinePlayers.ContainsKey(member.name)) {
                var player = onlinePlayers[member.name];
                player.guildName = guildName;
                player.guild = guild;
                player.TargetGuildSync(player.connectionToClient, guild);
            }
        }
    }

    // send guild notice and members to all observers would be bandwidth over-
    // kill, so we use a targetrpc
    [TargetRpc(channel=Channels.DefaultUnreliable)] // only send to one client
    public void TargetGuildSync(NetworkConnection target, Guild guild) {
        this.guild = guild;
    }

    // helper function to clear guild variables sync for kick/leave/terminate
    [Server]
    void ClearGuild() {
        guildName = "";
        guild = new Guild();
        TargetGuildSync(connectionToClient, guild);
    }

    [Server]
    public void SetGuildOnline(bool online) {
        if (InGuild()) {
            guild.SetOnline(name, online);
            BroadcastGuildChanges(guildName, guild);
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildInviteTarget() {
        // validate
        if (target != null && target is Player &&
            InGuild() && !((Player)target).InGuild() &&
            guild.CanInvite(name, target.name) &&
            Time.time >= nextRiskyActionTime &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange) {
            // send a invite and reset risky time
            ((Player)target).guildInviteFrom = name;
            nextRiskyActionTime = Time.time + guildInviteWaitSeconds;
            print(name + " invited " + target.name + " to guild");
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildInviteAccept() {
        // valid invitation?
        // note: no distance check because sender might be far away already
        if (!InGuild() && guildInviteFrom != "" &&
            onlinePlayers.ContainsKey(guildInviteFrom)) {
            // can sender actually invite us?
            var sender = onlinePlayers[guildInviteFrom];
            if (sender.InGuild() && sender.guild.CanInvite(sender.name, name)) {
                // add self to sender's guild members list
                sender.guild.AddMember(name, level);

                // broadcast and save changes from sender to everyone
                BroadcastGuildChanges(sender.guildName, sender.guild);
                print(sender.name + " added " + name + " to guild: " + guildName);
            }
        }

        // reset guild invite in any case
        guildInviteFrom = "";
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildInviteDecline() {
        guildInviteFrom = "";
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildKick(string memberName) {
        // validate
        if (InGuild() && guild.CanKick(name, memberName)) {
            // remove from member list
            guild.RemoveMember(memberName);

            // broadcast and save changes
            BroadcastGuildChanges(guildName, guild);

            // clear variables for the kicked person
            if (onlinePlayers.ContainsKey(memberName))
                onlinePlayers[memberName].ClearGuild();
            print(name + " kicked " + memberName + " from guild: " + guildName);
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildPromote(string memberName) {
        // validate
        if (InGuild() && guild.CanPromote(name, memberName)) {
            // promote the member
            guild.PromoteMember(memberName);

            // broadcast and save changes
            BroadcastGuildChanges(guildName, guild);
            print(name + " promoted " + memberName + " in guild: " + guildName);
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdGuildDemote(string memberName) {
        // validate
        if (InGuild() && guild.CanDemote(name, memberName)) {
            // demote the member
            guild.DemoteMember(memberName);

            // broadcast and save changes
            BroadcastGuildChanges(guildName, guild);
            print(name + " demoted " + memberName + " in guild: " + guildName);
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdSetGuildNotice(string notice) {
        // validate
        // (only allow changes every few seconds to avoid bandwidth issues)
        if (InGuild() && guild.CanNotify(name) &&
            notice.Length < Guild.NoticeMaxLength &&
            Time.time >= nextRiskyActionTime) {
            // set notice and reset next time
            guild.notice = notice;
            nextRiskyActionTime = Time.time + Guild.NoticeWaitSeconds;

            // broadcast and save changes
            BroadcastGuildChanges(guildName, guild);
            print(name + " changed guild notice to: " + guild.notice);
        }
    }

    // helper function to check if we are near a guild manager npc
    public bool IsGuildManagerNear() {
        return target != null &&
               target is Npc &&
               ((Npc)target).offersGuildManagement &&
               Utils.ClosestDistance(collider, target.collider) <= interactionRange;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdTerminateGuild() {
        // validate
        if (InGuild() && IsGuildManagerNear() && guild.CanTerminate(name)) {
            // remove guild from database
            Database.RemoveGuild(guildName);

            // clear player variables
            ClearGuild();
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdCreateGuild(string newGuildName) {
        // validate
        if (health > 0 && IsGuildManagerNear() && !InGuild() && gold >= Guild.CreationPrice) {
            if (Guild.IsValidGuildName(newGuildName) &&
                !Database.GuildExists(guildName)) { // db check only on server, no Guild.CanCreate function because client has no DB.
                // remove gold
                gold -= Guild.CreationPrice;

                // set guild and add self to members list as highest rank
                guildName = newGuildName;
                guild.notice = ""; // avoid null
                guild.AddMember(name, level, GuildRank.Master);

                // (broadcast and) save changes
                BroadcastGuildChanges(guildName, guild);
                print(name + " created guild: " + guildName);
            } else {
                string message = "Guild name invalid!"; // exists or invalid regex
                chat.TargetMsgInfo(connectionToClient, message);
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdLeaveGuild() {
        // validate
        if (InGuild() && guild.CanLeave(name)) {
            // remove self from members list
            guild.RemoveMember(name);

            // broadcast and save changes
            BroadcastGuildChanges(guildName, guild);

            // reset guild info and members list for the person that left
            ClearGuild();
        }
    }

    // party ///////////////////////////////////////////////////////////////////
    public bool InParty() {
        return party.members != null && party.members.Length > 0;
    }

    [Server]
    static void BroadcastPartyChanges(Party party) {
        // copy to every online member. we don't just reload from db because the
        // online status is only available in the members list
        foreach (string member in party.members) {
            if (onlinePlayers.ContainsKey(member)) {
                var player = onlinePlayers[member];
                player.party = party;
                player.TargetPartySync(player.connectionToClient, party);
            }
        }
    }

    // sending party info to all observers would be bandwidth overkill, so we
    // use a targetrpc
    [TargetRpc(channel=Channels.DefaultUnreliable)] // only send to one client
    public void TargetPartySync(NetworkConnection target, Party party) {
        this.party = party;
    }

    // helper function to clear party variables sync for kick/leave/dismiss
    [Server]
    void ClearParty() {
        party = new Party();
        TargetPartySync(connectionToClient, party);
    }

    // find party members in proximity for item/exp sharing etc.
    public List<Player> GetPartyMembersInProximity() {
        if (InParty()) {
            return netIdentity.observers.Select(conn => Utils.GetGameObjectFromPlayerControllers(conn.playerControllers).GetComponent<Player>())
                                        .Where(p => party.GetMemberIndex(p.name) != -1)
                                        .ToList();
        }
        return new List<Player>();
    }

    // party invite by name (not by target) so that chat commands are possible
    // if needed
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyInvite(string otherName) {
        // validate: is there someone with that name, and not self?
        if (otherName != name && onlinePlayers.ContainsKey(otherName) &&
            Time.time >= nextRiskyActionTime) {
            var other = onlinePlayers[otherName];

            // can only send invite if no party yet or party isn't full and
            // have invite rights and other guy isn't in party yet
            if ((!InParty() || party.CanInvite(name)) && !other.InParty()) {
                // send a invite and reset risky time
                ((Player)target).partyInviteFrom = name;
                nextRiskyActionTime = Time.time + partyInviteWaitSeconds;
                print(name + " invited " + target.name + " to party");
            }
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyInviteAccept() {
        // valid invitation?
        // note: no distance check because sender might be far away already
        if (!InParty() && partyInviteFrom != "" &&
            onlinePlayers.ContainsKey(partyInviteFrom)) {
            // can sender actually invite us?
            var sender = onlinePlayers[partyInviteFrom];

            // -> either he is in a party and can still invite someone
            if (sender.InParty() && sender.party.CanInvite(sender.name)) {
                sender.party.AddMember(name);
                BroadcastPartyChanges(sender.party);
                print(sender.name + " added " + name + " to " + sender.party.members[0] + "'s party");
            // -> or he is not in a party and forms a new one
            } else if (!sender.InParty()) {
                sender.party.AddMember(sender.name); // master
                sender.party.AddMember(name);
                BroadcastPartyChanges(sender.party);
                print(sender.name + " formed a new party with " + name);
            }
        }

        // reset party invite in any case
        partyInviteFrom = "";
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyInviteDecline() {
        partyInviteFrom = "";
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyKick(int memberIndex) {
        // validate: party master and index exists and not master?
        if (InParty() && party.members[0] == name &&
            0 < memberIndex && memberIndex < party.members.Length) {
            string member = party.members[memberIndex];

            // kick
            party.RemoveMember(member);

            // still enough people in it for a party?
            if (party.members.Length > 1) {
                BroadcastPartyChanges(party);
            } else {
                // a party requires at least two people, otherwise it's not
                // really a party anymore. if we'd keep it alive with one player
                // then he can't be invited to another party until he dismisses
                // the empty one.
                ClearParty();
            }

            // clear for the kicked person too
            if (onlinePlayers.ContainsKey(member))
                onlinePlayers[member].ClearParty();

            print(name + " kicked " + member + " from party");
        }
    }

    // version without cmd because we need to call it from the server too
    public void PartyLeave() {
        // validate: in party and not master?
        if (InParty() && party.members[0] != name) {
            // remove self from party
            party.RemoveMember(name);

            // still enough people in it for a party?
            if (party.members.Length > 1) {
                BroadcastPartyChanges(party);
            } else {
                // a party requires at least two people, otherwise it's not
                // really a party anymore. if we'd keep it alive with one player
                // then he can't be invited to another party until he dismisses
                // the empty one.
                if (onlinePlayers.ContainsKey(party.members[0]))
                    onlinePlayers[party.members[0]].ClearParty();
            }

            // clear for self
            ClearParty();
            print(name + " left the party");
        }
    }
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyLeave() { PartyLeave(); }

    // version without cmd because we need to call it from the server too
    public void PartyDismiss() {
        // validate: is master?
        if (InParty() && party.members[0] == name) {
            // clear party for everyone
            foreach (string member in party.members) {
                if (onlinePlayers.ContainsKey(member))
                    onlinePlayers[member].ClearParty();
            }
            print(name + " dismissed the party");
        }
    }
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartyDismiss() { PartyDismiss(); }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartySetExperienceShare(bool value) {
        // validate: is party master?
        if (InParty() && party.members[0] == name) {
            // set new value, sync to everyone else
            party.shareExperience = value;
            BroadcastPartyChanges(party);
        }
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPartySetGoldShare(bool value) {
        // validate: is party master?
        if (InParty() && party.members[0] == name) {
            // set new value, sync to everyone else
            party.shareGold = value;
            BroadcastPartyChanges(party);
        }
    }

    // pet /////////////////////////////////////////////////////////////////////
    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPetSetAutoAttack(bool value) {
        // validate
        if (activePet != null)
            activePet.autoAttack = value;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdPetSetDefendOwner(bool value) {
        // validate
        if (activePet != null)
            activePet.defendOwner = value;
    }

    [Command(channel=Channels.DefaultUnreliable)] // unimportant => unreliable
    public void CmdNpcRevivePet(int index) {
        // validate: close enough, npc alive and valid index and valid item?
        // use collider point(s) to also work with big entities
        if (state == "IDLE" &&
            target != null &&
            target.health > 0 &&
            target is Npc &&
            ((Npc)target).offersPetRevive &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange &&
            0 <= index && index < inventory.Count &&
            inventory[index].valid &&
            inventory[index].category == "Pet" &&
            inventory[index].petHealth == 0 &&
            inventory[index].petPrefab != null)
        {
            var item = inventory[index];

            // enough gold?
            if (gold >= item.petPrefab.revivePrice) {
                // pay for it, revive it
                gold -= item.petPrefab.revivePrice;
                item.petHealth = item.petPrefab.healthMax;
                inventory[index] = item;
            }
        }
    }

    // selection handling //////////////////////////////////////////////////////
    void SetIndicatorViaParent(Transform parent) {
        if (!indicator) indicator = Instantiate(indicatorPrefab);
        indicator.transform.SetParent(parent, true);
        indicator.transform.position = parent.position;
    }

    void SetIndicatorViaPosition(Vector3 position) {
        if (!indicator) indicator = Instantiate(indicatorPrefab);
        indicator.transform.parent = null;
        indicator.transform.position = position;
    }

    [Command(channel=Channels.DefaultReliable)] // important for skills etc.
    public void CmdSetTarget(NetworkIdentity ni) {
        // validate
        if (ni != null) {
            // can directly change it, or change it after casting?
            if (state == "IDLE" || state == "MOVING")
                target = ni.GetComponent<Entity>();
            else if (state == "CASTING")
                nextTarget = ni.GetComponent<Entity>();
        }
    }

    void SetIgnoreRaycast(Dictionary<Transform, int> layerBackups) {
        foreach (Transform tf in GetComponentsInChildren<Transform>(true)) {
            layerBackups[tf] = tf.gameObject.layer;
            tf.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
        }
    }

    void RestoreLayers(Dictionary<Transform, int> layerBackups) {
        foreach (Transform tf in GetComponentsInChildren<Transform>(true))
            tf.gameObject.layer = layerBackups[tf];
    }

    [Client]
    void SelectionHandling() {
        // click raycasting if not over a UI element & not pinching on mobile
        // note: this only works if the UI's CanvasGroup blocks Raycasts
        if (Input.GetMouseButtonDown(0) && !Utils.IsCursorOverUserInterface() && Input.touchCount <= 1) {
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            // ignore local player for raycasts if needed
            // => setting layer to IgnoreRaycasts before casting is the easiest way to do it
            // => raycast + !=this check would still cause hit.point to be on player
            // => raycastall is not sorted and child objects might have different layers etc.
            Dictionary<Transform, int> layerBackups = new Dictionary<Transform, int>();
            if (localPlayerClickThrough) SetIgnoreRaycast(layerBackups);

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit)) {
                // valid target?
                var entity = hit.transform.GetComponent<Entity>();
                if (entity) {
                    // set indicator
                    SetIndicatorViaParent(hit.transform);

                    // clicked last target again? and is not self or pet?
                    if (entity == target && entity != this && entity != activePet) {
                        // attackable? => attack
                        if (CanAttack(entity)) {
                            // cast the first skill (if any, and if ready)
                            if (skills.Count > 0 && skills[0].IsReady())
                                CmdUseSkill(0);
                            // otherwise walk there if still on cooldown etc
                            // use collider point(s) to also work with big entities
                            else
                                CmdNavigateTo(entity.collider.ClosestPointOnBounds(transform.position), skills.Count > 0 ? skills[0].castRange : 0f);
                        // npc & alive => talk
                        } else if (entity is Npc && entity.health > 0) {
                            // close enough to talk?
                            // use collider point(s) to also work with big entities
                            if (Utils.ClosestDistance(collider, entity.collider) <= interactionRange)
                                FindObjectOfType<UINpcDialogue>().Show();
                            // otherwise walk there
                            // use collider point(s) to also work with big entities
                            else
                                CmdNavigateTo(entity.collider.ClosestPointOnBounds(transform.position), interactionRange);
                        // monster & dead => loot
                        } else if (entity is Monster && entity.health == 0) {
                            // has loot? and close enough?
                            // use collider point(s) to also work with big entities
                            if (((Monster)entity).HasLoot() &&
                                Utils.ClosestDistance(collider, entity.collider) <= interactionRange)
                                FindObjectOfType<UILoot>().Show();
                            // otherwise walk there
                            // use collider point(s) to also work with big entities
                            else
                                CmdNavigateTo(entity.collider.ClosestPointOnBounds(transform.position), interactionRange);
                        }

                        // addon system hooks
                        Utils.InvokeMany(typeof(Player), this, "OnSelect_", entity);
                    // clicked a new target
                    } else {
                        // target it
                        CmdSetTarget(entity.netIdentity);
                    }
                // otherwise it's a movement target
                } else {
                    // set indicator and navigate to the nearest walkable
                    // destination. this prevents twitching when destination is
                    // accidentally in a room without a door etc.
                    var bestDestination = agent.NearestValidDestination(hit.point);
                    SetIndicatorViaPosition(bestDestination);
                    CmdNavigateTo(bestDestination, 0);
                }
            }

            // reset local ignore layers
            if (localPlayerClickThrough) RestoreLayers(layerBackups);
        }
    }

    // simple WSAD movement without prediction. uses at max 6KB/s when smashing
    // on all buttons at once, but uses almost nothing when just moving into one
    // direction for a while.
    Vector3 lastDestination = Vector3.zero;
    float lastTime = 0;
    [Client]
    void WSADHandling() {
        // get horizontal and vertical input
        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");
        if (horizontal != 0 || vertical != 0) {
            // don't move if currently typing in an input
            // we check this after checking h and v to save computations
            if (!UIUtils.AnyInputActive()) {
                // create input vector, normalize in case of diagonal movement
                var input = new Vector3(horizontal, 0, vertical);
                if (input.magnitude > 1) input = input.normalized;

                // get camera rotation without up/down angle, only left/right
                var angles = Camera.main.transform.rotation.eulerAngles;
                angles.x = 0;
                var rotation = Quaternion.Euler(angles); // back to quaternion

                // calculate input direction relative to camera rotation
                var direction = rotation * input;

                // send navigation request to server, but to save bandwidth we
                // can't do that every single time, instead only if:
                // - 50ms elapsed
                // - the direction has changed (e.g. upwards => downwards)
                // - the destination was almost reached (don't move,stand,move)
                //   we use speed/5 here to get the distance for 1s / 5 = 200ms
                var lastDirection = lastDestination - transform.position;
                bool directionChanged = direction.normalized != lastDirection.normalized;
                bool almostThere = Vector3.Distance(transform.position, lastDestination) < agent.speed / 5;
                if (Time.time - lastTime > 0.05f && (directionChanged || almostThere)) {
                    // set destination a bit further away to not sync that soon
                    // again. we multiply it by speed, because that sets it one
                    // second further away
                    // (because we move 'speed' meters per second)
                    var destination = transform.position + direction * agent.speed;

                    // try to move as far to that point as possible. for click
                    // movement we always try to find a valid path there, even
                    // if we click behind a wall. but for WSAD movement it feels
                    // a lot better to not try to run behind walls if there is
                    // no direct path there.
                    // => we use NavMesh.Raycast to find out if path is blocked
                    NavMeshHit hit;
                    if (NavMesh.Raycast(transform.position, destination, out hit, NavMesh.AllAreas))
                        destination = hit.position;

                    // navigate only if destination changed
                    if (destination != lastDestination) {
                        CmdNavigateTo(destination, 0);
                        lastTime = Time.time;
                        lastDestination = destination;
                    }
                }

                // clear indicator if there is one, and if it's not on a target
                // (simply looks better)
                if (indicator != null && indicator.transform.parent == null)
                    Destroy(indicator);
            }
        }
    }

    // simple tab targeting
    [Client]
    void TargetNearest() {
        if (Input.GetKeyDown(targetNearestKey)) {
            // find all monsters that are alive, sort by distance
            var objects = GameObject.FindGameObjectsWithTag("Monster");
            var monsters = objects.Select(go => go.GetComponent<Monster>()).Where(m => m.health > 0);
            var sorted = monsters.OrderBy(m => Vector3.Distance(transform.position, m.transform.position)).ToList();

            // target nearest one
            if (sorted.Count > 0) {
                SetIndicatorViaParent(sorted[0].transform);
                CmdSetTarget(sorted[0].netIdentity);
            }
        }
    }

    // drag and drop ///////////////////////////////////////////////////////////
    void OnDragAndDrop_InventorySlot_InventorySlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo

        // merge? (just check the name, rest is done server sided)
        if (inventory[slotIndices[0]].valid && inventory[slotIndices[1]].valid &&
            inventory[slotIndices[0]].name == inventory[slotIndices[1]].name) {
            CmdInventoryMerge(slotIndices[0], slotIndices[1]);
        // split?
        } else if (Utils.AnyKeyPressed(inventorySplitKeys)) {
            CmdInventorySplit(slotIndices[0], slotIndices[1]);
        // swap?
        } else {
            CmdSwapInventoryInventory(slotIndices[0], slotIndices[1]);
        }
    }

    void OnDragAndDrop_InventorySlot_TrashSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        CmdSwapInventoryTrash(slotIndices[0]);
    }

    void OnDragAndDrop_InventorySlot_EquipmentSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        CmdSwapInventoryEquip(slotIndices[0], slotIndices[1]);
    }

    void OnDragAndDrop_InventorySlot_SkillbarSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        skillbar[slotIndices[1]].reference = inventory[slotIndices[0]].name; // just save it clientsided
    }

    void OnDragAndDrop_InventorySlot_NpcSellSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        if (inventory[slotIndices[0]].sellable && !inventory[slotIndices[0]].petSummoned) {
            FindObjectOfType<UINpcTrading>().sellIndex = slotIndices[0];
            FindObjectOfType<UINpcTrading>().sellAmountInput.text = inventory[slotIndices[0]].amount.ToString();
        }
    }

    void OnDragAndDrop_InventorySlot_TradingSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        if (inventory[slotIndices[0]].tradable)
            CmdTradeOfferItem(slotIndices[0], slotIndices[1]);
    }

    void OnDragAndDrop_InventorySlot_CraftingIngredientSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        if (!craftingIndices.Contains(slotIndices[0]))
            craftingIndices[slotIndices[1]] = slotIndices[0];
    }

    void OnDragAndDrop_TrashSlot_InventorySlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        CmdSwapTrashInventory(slotIndices[1]);
    }

    void OnDragAndDrop_EquipmentSlot_InventorySlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        CmdSwapInventoryEquip(slotIndices[1], slotIndices[0]); // reversed
    }

    void OnDragAndDrop_EquipmentSlot_SkillbarSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        skillbar[slotIndices[1]].reference = equipment[slotIndices[0]].name; // just save it clientsided
    }

    void OnDragAndDrop_SkillsSlot_SkillbarSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        skillbar[slotIndices[1]].reference = skills[slotIndices[0]].name; // just save it clientsided
    }

    void OnDragAndDrop_SkillbarSlot_SkillbarSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        // just swap them clientsided
        var temp = skillbar[slotIndices[0]].reference;
        skillbar[slotIndices[0]].reference = skillbar[slotIndices[1]].reference;
        skillbar[slotIndices[1]].reference = temp;
    }

    void OnDragAndDrop_CraftingIngredientSlot_CraftingIngredientSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        // just swap them clientsided
        var temp = craftingIndices[slotIndices[0]];
        craftingIndices[slotIndices[0]] = craftingIndices[slotIndices[1]];
        craftingIndices[slotIndices[1]] = temp;
    }

    void OnDragAndDrop_InventorySlot_NpcPetReviveSlot(int[] slotIndices) {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        if (inventory[slotIndices[0]].category == "Pet")
            FindObjectOfType<UINpcPetRevive>().itemIndex = slotIndices[0];
    }

    void OnDragAndClear_SkillbarSlot(int slotIndex) {
        skillbar[slotIndex].reference = "";
    }

    void OnDragAndClear_TradingSlot(int slotIndex) {
        CmdTradeOfferItemClear(slotIndex);
    }

    void OnDragAndClear_NpcSellSlot(int slotIndex) {
        FindObjectOfType<UINpcTrading>().sellIndex = -1;
    }

    void OnDragAndClear_CraftingIngredientSlot(int slotIndex) {
        craftingIndices[slotIndex] = -1;
    }

    void OnDragAndClear_NpcPetReviveSlot(int slotIndex) {
        FindObjectOfType<UINpcPetRevive>().itemIndex = -1;
    }
}
