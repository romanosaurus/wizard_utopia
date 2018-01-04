﻿using UnityEngine;
using UnityEngine.UI;

public partial class UISkillbar : MonoBehaviour {
    public GameObject panel;
    public UISkillbarSlot slotPrefab;
    public Transform content;

    void Update() {
        var player = Utils.ClientLocalPlayer();
        panel.SetActive(player != null); // hide while not in the game world
        if (!player) return;

        // instantiate/destroy enough slots
        UIUtils.BalancePrefabs(slotPrefab.gameObject, player.skillbar.Length, content);

        // refresh all
        for (int i = 0; i < player.skillbar.Length; ++i) {
            var slot = content.GetChild(i).GetComponent<UISkillbarSlot>();
            slot.dragAndDropable.name = i.ToString(); // drag and drop index

            // hotkey overlay (without 'Alpha' etc.)
            string pretty = player.skillbar[i].hotKey.ToString().Replace("Alpha", "");
            slot.hotkeyText.text = pretty;

            // skill, inventory item or equipment item?
            int skillIndex = player.GetSkillIndexByName(player.skillbar[i].reference);
            int invIndex = player.GetInventoryIndexByName(player.skillbar[i].reference);
            int equipIndex = player.GetEquipmentIndexByName(player.skillbar[i].reference);
            if (skillIndex != -1) {
                var skill = player.skills[skillIndex];

                // hotkey pressed and not typing in any input right now?
                if (skill.learned && skill.IsReady() &&
                    Input.GetKeyDown(player.skillbar[i].hotKey) &&
                    !UIUtils.AnyInputActive()) {
                    player.CmdUseSkill(skillIndex);
                }

                // refresh skill slot
                slot.button.onClick.SetListener(() => {
                    player.CmdUseSkill(skillIndex);
                });
                slot.tooltip.enabled = true;
                slot.tooltip.text = skill.ToolTip();
                slot.dragAndDropable.dragable = true;
                slot.image.color = Color.white;
                slot.image.sprite = skill.image;
                float cd = skill.CooldownRemaining();
                slot.cooldownOverlay.SetActive(cd > 0);
                slot.cooldownText.text = cd.ToString("F0");
            } else if (invIndex != -1) {
                var item = player.inventory[invIndex];

                // hotkey pressed and not typing in any input right now?
                if (Input.GetKeyDown(player.skillbar[i].hotKey) && !UIUtils.AnyInputActive())
                    player.CmdUseInventoryItem(invIndex);

                // refresh inventory slot
                slot.button.onClick.SetListener(() => {
                    player.CmdUseInventoryItem(invIndex);
                });
                slot.tooltip.enabled = true;
                slot.tooltip.text = item.ToolTip();
                slot.dragAndDropable.dragable = true;
                slot.image.color = Color.white;
                slot.image.sprite = item.image;
                slot.cooldownOverlay.SetActive(item.amount > 1);
                slot.cooldownText.text = item.amount.ToString();
            } else if (equipIndex != -1) {
                var item = player.equipment[equipIndex];

                // refresh equipment slot
                slot.button.onClick.RemoveAllListeners();
                slot.tooltip.enabled = true;
                slot.tooltip.text = item.ToolTip();
                slot.dragAndDropable.dragable = true;
                slot.image.color = Color.white;
                slot.image.sprite = item.image;
                slot.cooldownOverlay.SetActive(false);
            } else {
                // clear the outdated reference
                player.skillbar[i].reference = "";

                // refresh empty slot
                slot.button.onClick.RemoveAllListeners();
                slot.tooltip.enabled = false;
                slot.dragAndDropable.dragable = false;
                slot.image.color = Color.clear;
                slot.image.sprite = null;
                slot.cooldownOverlay.SetActive(false);
            }
        }

        // addon system hooks
        Utils.InvokeMany(typeof(UISkillbar), this, "Update_");
    }
}