namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // Reviewed catalog-origin/titles.json SHA256 4b1d3945f489417e39b159f0338223ed1a3a8c1b117846c3dcb2a2cf0e6cb329
    // All names/descriptions are transcribed; bonuses remain owned by authored runtime policies.
    private static PostgresSchemaMigration CreateEditorTitleGrants() => new(
        "20261030_234_editor_title_grants",
        "Separate administrative title grants from earned title evidence and unify entitlement reads",
        """
        CREATE TABLE public.editor_title_catalog (
            title_id integer PRIMARY KEY CHECK (title_id > 0),
            display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            description text NOT NULL DEFAULT ''
        );
        COMMENT ON TABLE public.editor_title_catalog IS
            'Reviewed native client title identities eligible for explicit administrative grants; no inferred reward attributes.';
        INSERT INTO public.editor_title_catalog(title_id,display_name,description) VALUES
            (1000,'Valiant Spirit','Do no believe those who say they have no fears. True heroes know fear but does not let it control them.'),
            (1001,'Defender of Justice','Those fight against the forces of evil are champions of justice.'),
            (1002,'Conquest Pioneer','Divide and Conquer. Expand or Die.'),
            (1003,'Battle Expert','Cunning strategies are required to become a Battle Expert.'),
            (1100,'Wolf Blood','The ability to work in groups is the test of all great leaders.'),
            (1101,'Tiger Claw','Treat softly... and carry a powerful fist for times of trouble.'),
            (1102,'Lion Heart','Elegant yet packs a deadly force, the Lion is known for its graceful lethality.'),
            (1103,'Dragon Slayer','Dragons are reduced to merely lizards before your courage and skill!'),
            (1200,'Nature Child','A child of Nature is spared of many calamities.'),
            (1201,'Thunderbolt','The power of thunder flows from your every move.'),
            (1202,'Holy Spirit','The Gods shall live through your actions.'),
            (1203,'Luna''s Apprentice','Follow Luna''s words to become a powerful Priest.'),
            (1300,'Star Ray','Star rays are mystical forces used when casting spells.'),
            (1301,'Burning Soul','A hero''s soul hungers for freedom and release. But few have fully learned to control their power.'),
            (1302,'Raging Force','The force of rage courses through your veins.'),
            (1303,'Fire Guardian','Take heed of Hephaestus'' advice to become a famous Priest.'),
            (1401,'Engaged','Congratulations, you just became engaged!'),
            (1402,'Newly Wed','Congratulations, you just got married!'),
            (2000,'Athenian Soldier','Your success with the Village Head''s challenges have qualified you for Athenian military service.'),
            (2001,'Exterminator','No bug is safe when you''re near, great Exterminator!'),
            (2002,'Boar Killer','You killed the Boar King, all the boars will be looking for you now!'),
            (2003,'Budding Hero','You are on the road to becoming a real hero after completing the first test of the Hero''s Trial.'),
            (2004,'Athenian Warrior','Every warrior must fulfil their duty as a protector of their homeland.'),
            (2005,'Bounty Hunter','No task is too hard, and no reward is too great.'),
            (2500,'Spartan Soldier','Your success with the Village Head''s challenges have qualified you for Spartan military service.'),
            (2501,'Exterminator','No bug is safe when you''re near, great Exterminator!'),
            (2502,'Wolf Killer','You killed the Wolf King, all the wolves will be looking for you now!'),
            (2503,'Budding Hero','You are on the road to becoming a real hero after completing the first test of the Hero''s Trial.'),
            (2504,'Spartan Warrior','Every warrior must fulfil their duty as a protector of their homeland.'),
            (2505,'Bounty Hunter','No task is too hard, and no reward is too great.'),
            (2506,'Greek Vanguard','Your valor is much needed on the frontlines in Thermophylae.'),
            (3000,'Lion Blood','The ability to work in groups is the test of all great leaders.'),
            (3001,'Dragon Soul','Let your fiery dragon spirit fly free in the face of danger!'),
            (3002,'Popeye','Spinach is the key.'),
            (3003,'Demi God','Your supreme strength is comparable to that of Hercules!'),
            (3004,'Messenger of Doom','Those who see you know that the end is near.'),
            (3005,'Holy Wraith','Unleash the power of the Gods on your enemies.'),
            (3006,'Iron Bastion','Impenetrable defense will bring your victory on the battlefield.'),
            (3007,'Heavenly Shield','An unshakable shield can hold back physical attacks from lesser men.'),
            (3008,'Magical Barrier','Common magical attacks will never fein a great hero of your ranks.'),
            (3009,'The Favored Disciple','Your patronage to the Gods have won their favor and protection from enemy magical attacks.'),
            (3010,'Sharpshooter','Well if you''ve ever missed a shot, no one is around to tell the tale!'),
            (3011,'Deadly Strike','Even a hovering mosquito is no match for your shooting skills!'),
            (3012,'Captain Falcon','Swift and agile like a falcon, you are not an easy target to capture.'),
            (3013,'The Limber Hero','Flexbility is key to success on the battlefield and in entertaining friends.'),
            (3014,'Cool as Ice','Surrounded by the chill of ice, damage cannot reach you.'),
            (3015,'Blessed Light','The Gods smile upon your heroism and grant you divine protection.'),
            (5000,'Vanguard Guild','A glorious title for guilds that performed outstandingly in the Guild Defensive War.'),
            (5001,'The Flash','Only players who got the first prize in the race during the week can obtain this title.'),
            (5002,'Master Jogger','Only players who got the second prize in the race during the week can obtain this title.'),
            (5003,'Treadmill Expert','Only players who got the third prize in the race during the week can obtain this title.'),
            (5004,'Speedy Sportsman','Only players who ranked the top ten in the race during the week can obtain this title.'),
            (5005,'Natural Born Killer','For your outstanding performance with the Most Enemy Kills on the Pindus Mountain Battlefield.'),
            (5006,'Battlefield Slayer','For your outstanding performance among the Top 5 players with the Most Enemy Kills on the Pindus Mountain Battlefield.'),
            (5007,'Pindus Champion','For your outstanding performance with the Best Score on the Pindus Mountain Battlefield.'),
            (5008,'Pindus Top 5','For your outstanding performance among the Top 5 Scorers on the Pindus Mountain Battlefield.'),
            (5009,'Medusa Executioners','To players whose team has accumulated 3,000 points within 10 minutes of the Medusa challenge.'),
            (5010,'Medusa Slayers','To players whose team has accumulated 3,000 points within 15 minutes of the Medusa challenge.'),
            (5011,'Medusa Challengers','To players whose team has accumulated 3,000 points within 20 minutes of the Medusa challenge.'),
            (5012,'Generous Envoy','Feel free to contact a GM to get this newbie tutor title.'),
            (5013,'Seabed Explorer','The proof of exploring the seabed with an excellent team.'),
            (5014,'Deep Sea Hunter','The proof of exploring the seabed alone.'),
            (5015,'Pride of Athens','The title won for winning First Place in the Ni Mini Valley.'),
            (5016,'Pride of Sparta','The title won for winning First Place in the Ni Mini Valley.'),
            (5017,'Guardian of Greece','Exclusively for 1-star VIP players.'),
            (5111,'Skull Crusher','A title won for slaying Cerberus, the Level 120 boss in Bonestraw Lair.'),
            (5112,'Hero of Lelantine','A title awarded to those who score the highest in their faction during the battle at Lelantine Farm.'),
            (5113,'Rhodes Scholar','The description of the title of Rhodes Scholar.'),
            (5114,'Wonderland Explorer','The title earned in the 2nd isle of Wonderland.'),
            (5115,'Wonderland Fighter','The title earned in the 4th isle of Wonderland.'),
            (5116,'Wonderland Savior','The title earned in the 6th isle of Wonderland.'),
            (5117,'Wonderland Envoy','The title earned in the 7th isle of Wonderland.'),
            (5118,'Wonderland Conquerer','The title earned in the 8th isle of Wonderland.'),
            (5119,'Holy Star','Exclusively for 2-star VIP players.'),
            (5120,'Orion the Magnificent','Exclusively for 3-star VIP players.'),
            (5121,'Legendary Hero','Exclusively for 4-star VIP players.'),
            (5122,'GodsWar Elite','Exclusively for 5-star VIP players.'),
            (5123,'Top Greek Guild','The best scoring guild in the Trojan Expedition. Lasts a week.'),
            (5124,'Theban Guardian','You''ve made great contributions to the alliance of Athens and Thebes.'),
            (5125,'Mycenaean Guardian','You''ve made great contributions to the alliance of Sparta and Mycenae.'),
            (5126,'Ahriman Slasher','You''ve become one of the most powerful evil beings ever.'),
            (5127,'Guardian of Mycenae','Awarded in recognition to one who has play significant roles in forging the alliance of Sparta and Mycenae. Bravo!'),
            (5128,'God Slayer','You are no mere mortal, for you have slain even the most powerful and evil of gods. Hail!'),
            (5129,'Divine Guardian','A special title for those who have made great contributions to GWO.'),
            (5130,'Chosen One','Awarded to those who have made significant contributions to GodsWar Online.'),
            (5131,'Sacred Healer','For your outstanding performance with the Most Healing done on the Pindus Mountain Battlefield.'),
            (5132,'Gaia''s Envoy','For your outstanding performance among the Top 5 players with the Most Healing done on the Pindus Mountain Battlefield.'),
            (5150,'Primordial One','Granted to those who walked the world before it was whole. Testers who helped shape the Origin.'),
            (5151,'Seeker of the Flaws','Bestowed upon those who saw beyond the veil, relentless bug hunters who uncovered the cracks in creation.'),
            (6000,'GodsWar Goddess','Congratulations on your title as the newest and cutest GodsWar Goddess! Just remember that a true goddess never breaks a heart lightly!');
        CREATE TABLE public.character_title_grants (
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            title_id integer NOT NULL REFERENCES public.editor_title_catalog(title_id),
            granted_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            granted_by text NOT NULL CHECK (length(granted_by) BETWEEN 1 AND 128),
            note text NOT NULL DEFAULT '' CHECK (length(note) <= 512),
            PRIMARY KEY(character_id,title_id)
        );
        COMMENT ON TABLE public.character_title_grants IS
            'Explicit admin title entitlements, separate from earned Medusa, Atlantis and Wonderland reward evidence.';
        CREATE VIEW public.character_title_entitlements AS
            SELECT character_id,title_id FROM public.character_title_ownership
            UNION SELECT character_id,title_id FROM public.atlantis_character_title_ownership
            UNION SELECT character_id,title_id FROM public.wonderland_character_title_ownership
            UNION SELECT character_id,title_id FROM public.character_title_grants;
        COMMENT ON VIEW public.character_title_entitlements IS
            'One deduplicated entitlement set shared by login snapshots and in-game title selection; removing a grant never removes an earned title.';

        CREATE FUNCTION public.advance_admin_title_grants_insert() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE public.character_base SET medusa_reward_revision=medusa_reward_revision+1
            WHERE id IN (SELECT character_id FROM new_title_grants);
            RETURN NULL;
        END; $$;
        CREATE TRIGGER advance_admin_title_grants_insert
            AFTER INSERT ON public.character_title_grants
            REFERENCING NEW TABLE AS new_title_grants
            FOR EACH STATEMENT EXECUTE FUNCTION public.advance_admin_title_grants_insert();

        CREATE FUNCTION public.advance_admin_title_grants_delete() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            UPDATE public.character_base c SET
                selected_title_id=CASE WHEN c.selected_title_id <> 0 AND NOT EXISTS(
                    SELECT 1 FROM public.character_title_entitlements e
                    WHERE e.character_id=c.id AND e.title_id=c.selected_title_id)
                    THEN 0 ELSE c.selected_title_id END,
                medusa_reward_revision=medusa_reward_revision+1
            WHERE c.id IN (SELECT character_id FROM old_title_grants);
            RETURN NULL;
        END; $$;
        CREATE TRIGGER advance_admin_title_grants_delete
            AFTER DELETE ON public.character_title_grants
            REFERENCING OLD TABLE AS old_title_grants
            FOR EACH STATEMENT EXECUTE FUNCTION public.advance_admin_title_grants_delete();
        """);
}
