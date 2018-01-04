using UnityEngine;
using UnityEngine.UI;

public partial class UINpcPetRevive : MonoBehaviour {
    public GameObject panel;
    public UIDragAndDropable itemSlot;
    public Text costsText;
    public Button reviveButton;
    [HideInInspector] public int itemIndex = -1;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        if (!player) return;

        // use collider point(s) to also work with big entities
        if (player.target != null && player.target is Npc &&
            Utils.ClosestDistance(player.collider, player.target.collider) <= player.interactionRange) {
            var npc = (Npc)player.target;

            // sell
            if (itemIndex != -1 && itemIndex < player.inventory.Count &&
                player.inventory[itemIndex].valid &&
                player.inventory[itemIndex].category == "Pet" &&
                player.inventory[itemIndex].petPrefab != null) {
                var item = player.inventory[itemIndex];

                itemSlot.GetComponent<Image>().color = Color.white;
                itemSlot.GetComponent<Image>().sprite = item.image;
                itemSlot.GetComponent<UIShowToolTip>().enabled = true;
                itemSlot.GetComponent<UIShowToolTip>().text = item.ToolTip();
                costsText.text = item.petPrefab.revivePrice.ToString();
                reviveButton.interactable = item.petHealth == 0 && player.gold >= item.petPrefab.revivePrice;
                reviveButton.onClick.SetListener(() => {
                    player.CmdNpcRevivePet(itemIndex);
                    itemIndex = -1;
                });
            } else {
                // show default sell panel in UI
                itemSlot.GetComponent<Image>().color = Color.clear;
                itemSlot.GetComponent<Image>().sprite = null;
                itemSlot.GetComponent<UIShowToolTip>().enabled = false;
                costsText.text = "0";
                reviveButton.interactable = false;
            }
        } else panel.SetActive(false); // hide

        // addon system hooks
        Utils.InvokeMany(typeof(UINpcPetRevive), this, "Update_");
    }
}
