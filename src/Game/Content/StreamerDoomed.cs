using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2. Registered by the game's own mod-type scan
/// (ModelDb.AllAbstractModelSubtypes unions ReflectionHelper.GetSubtypesInMods), id
/// STREAMER_DOOMED from the class name. Enchantments need no pool. OnPlay applies Doom
/// to the card's owner (the Inky idiom with the owner as target). Amount (the Doom per
/// play) is set by the granting relic through CardCmd.Enchant. Not stackable: one
/// enchantment per card, as every vanilla enchantment.</summary>
public sealed class StreamerDoomed : EnchantmentModel {
    public override bool HasExtraCardText => true;
    protected override IEnumerable<IHoverTip> ExtraHoverTips => new[] { HoverTipFactory.FromPower<DoomPower>() };

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay) {
        var owner = Card.Owner.Creature;
        await PowerCmd.Apply<DoomPower>(choiceContext, owner, Amount, owner, Card);
    }
}
