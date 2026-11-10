"""Native attribute-stone families, separate from grade-indexed value tables.
Source: State/GearEnhancementMaterialCatalog.cs and ClassSuitAttributePlanner.cs.
Tests compare every shipped Stone record to this policy; unknown IDs stay distinct.
"""
# stone, name, attribute IDs, Quartz-enhanceable
ORDINARY = [
 (9930,'Strength Stone',list(range(0,5)),True),(9931,'Shield Stone',list(range(10,15)),True),
 (9932,'Magic Stone',list(range(20,25)),True),(9933,'Spell Stone',list(range(30,35)),True),
 (9934,'Absorption Stone',list(range(100,105)),True),(9935,'Health Stone',list(range(130,135)),True),
 (9936,'Mana Stone',list(range(140,145)),True),(9937,'Blood Stone',list(range(150,155)),True),
 (9938,'Vigor Stone',list(range(160,165)),True),
]
ORDINARY += [(9940+n,name,[aid],False) for n,(name,aid) in enumerate([
 ('Accuracy Stone',40),('Psychic Stone',50),('Fury Stone',60),('Tenacity Stone',70),
 ('Impact Stone',80),('Fervor Stone',90),('Punishment Stone',110),('Purge Stone',120),
 ('Guard Stone',170),('Restoration Stone',180)])]
CLASS = [(9950+n,name,[aid],False) for n,(name,aid) in enumerate([
 ('Primal Stone',200),('Courage Stone',201),('Energy Stone',210),('Rage Stone',211),
 ('Holy Stone',220),('Blessing Stone',221),('Rune Stone',230),('Force Stone',231)])]
ORDINARY += [(9958,'Spirit of Destruction',[240],False),(9959,'Spirit of Penetration',[250],False)]
MOUNT = [(9970+n,name,list(range(300+10*n,308+10*n)),False) for n,name in enumerate([
 'Stone of Vitality','Stone of Wisdom','Stone of Precision','Stone of Evasion',
 'Stone of Strength','Stone of Sorcery','Stone of Wrath','Stone of Arcana',
 'Stone of Renewal','Stone of Serenity','Stone of Ruin','Stone of Negation',
 'Stone of Force','Stone of Essence','Stone of Fury','Stone of Impact'])]


def policy(aid):
    aid=int(aid)
    for group,records in [('ordinary',ORDINARY),('class',CLASS),('mount',MOUNT)]:
        for stone,name,chain,quartz in records:
            if aid in chain:
                return {'family':str(stone),'stone':stone,'stone_name':name,'group':group,'chain':chain,
                        'rank':chain.index(aid)+1 if quartz else None,'max_rank':len(chain) if quartz else None}
    return {'family':'unknown:%d'%aid,'group':'elemental' if 480<=aid<=500 else 'unknown',
            'chain':[aid],'rank':None,'max_rank':None}


def class_allowed(aid,profession):
    return (profession in (0,1) and aid in (200,201,210,211)) or (profession in (2,3) and aid in (220,221,230,231))
