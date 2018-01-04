// Note: this script has to be on an always-active UI parent, so that we can
// always find it from other code. (GameObject.Find doesn't find inactive ones)
using UnityEngine;
using UnityEngine.UI;

public partial class UITarget : MonoBehaviour {
    public GameObject panel;
    public Slider healthSlider;
    public Text nameText;
    public Button tradeButton;
    public Button guildInviteButton;
    public Button partyInviteButton;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        if (player.target != null && player.target != player) {
            float distance = Utils.ClosestDistance(player.collider, player.target.collider);

            // name and health
            panel.SetActive(true);
            healthSlider.value = player.target.HealthPercent();
            nameText.text = player.target.name;

            // trade button
            if (player.target is Player) {
                tradeButton.gameObject.SetActive(true);
                tradeButton.interactable = player.CanStartTradeWith(player.target);
                tradeButton.onClick.SetListener(() => {
                    player.CmdTradeRequestSend();
                });
            } else tradeButton.gameObject.SetActive(false);

            // guild invite button
            if (player.target is Player && player.InGuild()) {
                guildInviteButton.gameObject.SetActive(true);
                guildInviteButton.interactable = !((Player)player.target).InGuild() &&
                                                 player.guild.CanInvite(player.name, player.target.name) &&
                                                 NetworkTime.time >= player.nextRiskyActionTime &&
                                                 distance <= player.interactionRange;
                guildInviteButton.onClick.SetListener(() => {
                    player.CmdGuildInviteTarget();
                });
            } else guildInviteButton.gameObject.SetActive(false);

            // party invite button
            if (player.target is Player) {
                partyInviteButton.gameObject.SetActive(true);
                partyInviteButton.interactable = (!player.InParty() || player.party.CanInvite(player.name)) &&
                                                 !((Player)player.target).InParty() &&
                                                 NetworkTime.time >= player.nextRiskyActionTime &&
                                                 distance <= player.interactionRange;
                partyInviteButton.onClick.SetListener(() => {
                    player.CmdPartyInvite(player.target.name);
                });
            } else partyInviteButton.gameObject.SetActive(false);
        } else panel.SetActive(false); // hide

        // addon system hooks
        Utils.InvokeMany(typeof(UITarget), this, "Update_");
    }
}
