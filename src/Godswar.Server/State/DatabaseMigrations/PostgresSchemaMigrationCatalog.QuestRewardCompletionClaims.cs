namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateQuestRewardCompletionClaims() =>
        new(
            "20261007_231_quest_reward_completion_claims",
            "Allow daily quest item rewards once per completion",
            """
            ALTER TABLE public.quest_reward_item_claims
                ADD COLUMN completion_id uuid NOT NULL
                    DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE public.quest_reward_item_claims
                DROP CONSTRAINT quest_reward_item_claims_pkey;
            ALTER TABLE public.quest_reward_item_claims
                ADD PRIMARY KEY (character_id, quest_id, slot_index, completion_id);
            COMMENT ON COLUMN public.quest_reward_item_claims.completion_id IS
                'Zero for a one-time quest; a distinct identity for each daily or repeat quest completion.';
            """);
}
