using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // The Athens merchant quarter, captured from the reference server on
    // 2026-09-24 between 23:29 and 23:36. Each of these npcs answers an
    // ordinary click with function number zero and then streams its stock
    // straight after the client's 10068 open request, so the merchant needs a
    // catalog and no dialog at all.
    //
    // Every byte below is the reference's own opcode-10071 stream; only the npc
    // id and the live balance are patched at egress, exactly as the older
    // captured vendors do. The charge currency rides in each frame's header byte
    // 9 and resolves through TryGetShopCurrency(byte): 2 gold, 3 silver,
    // 4 bound gold.
    //
    // Athens_095 (npc 5233) is deliberately absent: its frame carries 0x01, a
    // currency code the runtime table does not define.

    // npc 5166: 5 frames, 63 records, currency 0x04 (BindingGold)
    private const string WarriorEquipmentVendorCatalogGzip =
        "H4sIAAAAAAACCrXWOU7DUBRG4efhBiOEREHFBugQBWMYOzaAxCKo6Jgh0FAhNkHFBhjCFFgAO6BlHiUqwEQhkZDyJxQ+z4Ul2/L3riz7yDs2093T6ZyLO4Ly3t1EzqViCwJXuf7fFoVpmv9zXLvpFnALwr0D3KJw7wH3Q7gPgNsb1LuPgDsr3CfA3RXuM+BeC/cFcLvCevcVcKeF+wa428J9B9w94c6bnz4smJ8+LJqfPiyZnz5chbUeJ5XjZfPTixXz04tV89OLNfPTi3Xz04uC+enFhvnpxab56cVW/Ps+B3F75dRnkm2duLzOlPhuotbsrurHFzCv6kcMzKv6YYCrevENPAfVixwwr+pFCsyretECzKt64QBX9SIB3DnhBoCrOhQCbrMOhdUO9WV8P6zBf0w/4KoODQCu6tAg4KoODQGu6tAw4KoO5QFXdWgEcFWHRgFXdWgMcFWHJgFXdWgccFWHJgC3WYeiaof227Ktk2vwP3QKuKpDB4CrOnQGuKpD54CrOnQIuKpDJcBVHToCXNWhC8BVHSoCrurQJeCqDh0DrurQCeCqDv0AWPzWv/gVAAA=";

    // npc 5167: 5 frames, 63 records, currency 0x04 (BindingGold)
    private const string ScholarEquipmentVendorCatalogGzip =
        "H4sIAAAAAAACCrXWOU7DQBiG4bHjZBJCICwVF6CEmgtwASRa9i00HIGOClGyrwkEwtKyhJ0DcANuQWsik0hI+RIKvzOFpbHl52/sV7ORHB8c6jfGBHmvdjXFlDGhWJ5nouf/rYQfhiN/9o2XSoC7KtxTwK0K9wxwv4VbBtxhr9k9B9yCcC8AtyTcCuB+CfcScAf8ZvcKcMeEew2468K9AdyKcNPWTR8y1k0fOqybPmStmz58+o0ep6N9p3XTi5x104su66YX3dZNL/LWTS96rJte9Fo3veizbnqxFvx+z16Qi25NZOLNCWpzRsV/Mw+4qh+TgKv6sQC4qh+LgKt6MQW4qhdLgKt6MQ24qhcFwFW9mAFc1YtlwF0R7izgqg7NAW67Dvn1Dm2m481JtjjHbAGu6tA24KoO7QCu6tAu4KoO7QGu6tA+4KoOHQCu6tAh4KoOHQGu6lARcFWHjgFXdegEcNt1KFHv0G023pxUi/PQM+CqDt0BrurQC+CqDr0CrurQPeCqDr0BrurQA+CqDr0DrupQFXBVhz4AV3XoEXBVh54AV3XoB0Gibwr4FQAA";

    // npc 5168: 6 frames, 76 records, currency 0x04 (BindingGold)
    private const string JewelryEquipmentVendorCatalogGzip =
        "H4sIAAAAAAACCrXWOVICURSF4W7ABJ5PVJwQ5wGccYjcgBtAcdbcyB2AIxpYbsLILbgQMxPnOTJFbKDKkkMZcM4LqOru4nvR/evmAul4MuI4TqDeLfw6MeM4eXBc1/G+/3cafPn8/K/n8p8GCG4WuF0E9wq4gwT3C7hDBDfpVrrdBHcHuMME9wK4PQT3Brhxghv1Vbq9BDcF3ATB3QVuH8E9A24/wb0E7rW/2CE3EPJeZWq8J1y4ZwHMX5bgol7sidx9govm+kDkHhJcNNdHIveY4KK5zoncE4KL5vpU5J7XFefaFwh7rxLB2u5prLJfTBFctF+MEFw010mCi/aLaYKL9otRgot6MUNw0X4xRnBRL2YJLtovxgku6sUcwUX7xQTBRb2YJLhov7g1mu7cGU137o2mOxl/ucdB7/nBaDr0aDQdejKaDj0bTYdejKZDr0bToTej6dC70XTow2g69Gk0HSrvQ/7SPpQK1XZPsEqXFgku6tISwUVdShNc1KFlgos6tEJwUYdWCS7q0BrBRR1aJ7ioQxsEF3Vok+CiDm0RXNShbYL7c/66YavpTqPVdKfJarpT3of8pX2o2Wo6FLGaDrVYTYdaraZDbVbToXar6VCH1XQoajUd6rSaDsUILtqHvgE2zkZggBoAAA==";

    // npc 5169: 7 frames, 91 records, currency 0x04 (BindingGold)
    private const string ArmorEquipmentVendorCatalogGzip =
        "H4sIAAAAAAACCrXXuU4bURjF8RnbCOLou2JLWHtaaHkCIGGNWcLeU/EGoaNgcQJZXoCKBBJIgIKHYM0CSLwC+y45YMYSgmMofM4tRpoZzf9W89N341ntFVWFnudFcv3rqzcS9bwEWL7vJd8/tbJDiUT1nfvURxOE7hDojhK6y6A7SeiegO5HQrfSf9gdI3QHQPcToTsFuuOE7i7ofiZ0S0MPu3FCNwa6XwjdQdB9T+iOge4HQncadGtM406tadypM40778Ipj6PJ+1emcei1aRyqN41DDaZxqNE0DjWZxqFm0zjUYhqH3pjGoZhpHBqO3P5/fsSSj1ayM9snJ41Lq4QucmmN0EUurRO6yKENQhc5tEnoIod+E7rIoT+ELnLoL6GLHPpH6CKHtghd5NA2oYsc2iF0kUPx4FwWCs5lexnOXc/SOLRP6CKHDghd5NAhoYscOiJ0kUPHhC5y6ITQRQ6dErrIoTNCFzl0Tugihy4IXeTQJaGLHLoidG/W/e5X07jzzTTuzJjGndS5LBScy2ZN49B30zj0wzQOzZnGoXnTOPTTNA79Mo1DC6ZxaNE0Di0Ruo/NQ+FgHoo9z2yfaBqXWgld5FIboYtcaid0kUMdhC5y6C2hixzqJHSRQ12ELnKom9BFDvUQusihXkIXOdRH6CKH+gldNA/lOo07eU7jTr7TuJOah8LBPFTgNA4VOo1DL5zGoZdO41CR0zhU7DQOlTiNQ6VO41CZ0zhUTuiieeg/+tkAQ7gfAAA=";

    // npc 5234: 5 frames, 63 records, currency 0x04 (BindingGold)
    private const string WarriorSupplierCatalogGzip =
        "H4sIAAAAAAACCrXWOU7DUBRG4efhBiOEREHFBmgpGMPYsQEkFkEFFTMEGirEJqjYAEOYAgtgB7TMo0QFmCgkElL+hMLnubBkW/7elWUfecemu+c6nXNxR1Deu5vIuVRsQeAq1//bojBN83+OazfdAm5BuHeAWxTuPeB+CPcBcHuCevcRcGeE+wS4u8J9Btxr4b4AbldY774C7pRw3wB3W7jvgLsn3Hnz04cF89OHRfPThyXz04ersNbjpHK8bH56sWJ+erFqfnqxZn56sW5+elEwP73YMD+92DQ/vdiKf9/nIG6vnPpMsq0Tl9eZFN9N1JrdVf34AuZV/YiBeVU/DHBVL76B56B6kQPmVb1IgXlVL1qAeVUvHOCqXiSAOyvcAHBVh0LAbdahsNqh3ozvhzX4j+kDXNWhfsBVHRoAXNWhQcBVHRoCXNWhPOCqDg0DrurQCOCqDo0CrurQBOCqDo0BrurQOOA261BU7dB+W7Z1cg3+h04BV3XoAHBVh84AV3XoHHBVhw4BV3WoBLiqQ0eAqzp0AbiqQ0XAVR26BFzVoWPAVR06AVzVoR9IUMI++BUAAA==";

    // npc 5237: 8 frames, 86 records, currency 0x04 (BindingGold)
    private const string SkillMerchantCatalogGzip =
        "H4sIAAAAAAACCrXXz0tUURjG8Xvv+KNmbBQbxdRstEDRQEMXtdBF0H7AQKGwIEFBYQYyUEFwKRh0IUFBQaEWtSjErbsWIa3mD3ChRC1ykUQQGGQ2zoXwfS4u5nvvYpjDDJ9zOJx7nvf1y4fan6UcxymrcU8+nXt1jnMsHtd1Cr+f9yS8k//+Nw6+dwHujnD7AfeDa93rEbm9EbkNgLtw0bo3ATcr3LuAmxfuDcBtS1j3NuBmhNsHuB+rrDsQkdsEuDXV1u0G3M2Ude8A7o9a6zYT50Gsd8cN7uPywrgdmMevt/NcA9yeZuv2AG6ncDsAd/uqddOAO9tiXb+Yq24xV2vrS5unKiRX44CrcnWvrnR3V7h/AHdc5N8XwFW5+j0i9xvg5j3r/gJcv9K6x4CbuWDdBHB+c6IO+Ensr3D3AVfl6mFErgfsb17k9VdgvbeS1j0A3M/CnfaC+/j0cCeBfVH1Rgxw/9WwZ90jYF9yDdatANar6oBqwFV1wG9gH1ReVwLrPUiH1wFesQ7YulzaPJdC6oBVwFV1wEvAVfn3LiL3FeCqXF0AXNVfvwFclddrgKv69iXATYo++D3gzov8WwRc1a++BtwpkVNvAXdDrHedeC9EH/wccFUfvEy8F6LvzbnBfVxWGG8Q9+YVO88LwFW5ugK4m6IP9gH3U2t4/sWK+TeXKm2eZEj+PQFclX8PAFfl1NOI3GnAVfmXAdw1kScTgNsXs+4o4Kr8ewi4R3HrTkbkTgGu6isHAVfl6gzgxoWbJd4LkX9jgKv6yPuA6wg37QX3cUVh/AiYR+Ys4I40WncIcFWujgNut3AfE+dD5PUwcW+KvP4LGaDALBAeAAA=";

    // npc 5240: 6 frames, 78 records, currency 0x04 (BindingGold)
    private const string ArmorMerchantCatalogGzip =
        "H4sIAAAAAAACCrXWN04DQRjF8V0HgawZCSQqLkAGAyaZWJKjJQ5BRUcJnQswJpucK67AXWjIuaI1yOxKSH4Whd+bYqTZ1fyn++lLhmarFiocxwlZ92d3YqWOkwXLdZ3c//9WOJDNxv+c/UtthO4S6LYTuteg20HofoFuJ6EbdfO7XYTuHOjGCd1L0O0mdG9At4fQrQzkd3sJ3QToDhC686DbR+imQLef0L0C3aTnkOs5lBE5tCtyaE/k0L7IoQORQ4cih45EDh2LHDoROXQqcuhC5NCZyKFzkUPp8K9DgVBZ7lN1pLh3ygs41EToIodqCF3kUJTQRQ41E7rIoVpCFznUQugih+oIXeRQK6GLHKondJFDMUIXOdRA6CKHGgld5NCt0bhzZzTu3BuNO4tB3+NI7vxgNA49Go1DT0bj0LPROPRiNA69Go1Db0bj0LvROPRhNA59Go1D/jwU9Oah5SK9Kyng0gahi1xaIXSRS5uELnJoi9BFDqUIXeTQNqGLHFoldJFDO4QucihN6CKHMoQucmiN0EUOrRO6yKFBq3FnyGrcGbYad/x5KOjNQyNW49Co1Tg0ZjUOjVuNQxNW49Ck1Tg0ZTUOTVuNQzNW41DCahz6Bt8MgZkwGwAA";

    // npc 5245: 5 frames, 63 records, currency 0x04 (BindingGold)
    private const string ScholarSupplierCatalogGzip =
        "H4sIAAAAAAACCrXWOU7DQBiG4bHjZBJCICwVF6Cl5gJcAImWfQsNB6Cgo0KU7GsCgbC0LGHnANyAW9CayCQSUr6Ewu9MYWls+fkb+9VsJMcGV/qNMUHeq11NMWVMKJbnmej5fyvhh+Hwn33jpRLgrgr3FHCrwj0D3G/hlgF3yGt2zwG3INwLwC0JtwK4X8K9BNwBv9m9AtxR4V4D7rpwbwC3Ity0ddOHjHXThw7rpg9Z66YPn36jx+lo32nd9CJn3fSiy7rpRbd104u8ddOLHuumF73WTS/6rJterAW/37MX5KJb45l4c4LanBHx38wBrurHBOCqfswDrurHAuCqXkwCrurFIuCqXkwBrupFAXBVL6YBV/ViCXCXhTsDuKpDs4DbrkN+vUOb6Xhzki3OMVuAqzq0DbiqQzuAqzq0C7iqQ3uAqzq0D7iqQweAqzp0CLiqQ0eAqzpUBFzVoWPAVR06Adx2HUrUO3SbjTcn1eI89Ay4qkN3gKs69AK4qkOvgKs6dA+4qkNvgKs69AC4qkPvgKs6VAVc1aEPwFUdegRc1aEnwFUd+gHmPygl+BUAAA==";

    // npc 5246: 2 frames, 24 records, currency 0x02 (Gold)
    private const string JewelryMerchantCatalogGzip =
        "H4sIAAAAAAACCutgCVevE2FgYGDiZQSSDDI8DAz/sQBGRgawPCHAz/T/vxUSH6ZJmQrmNmIxV5YK5u7BYq4KFcz9hsVcVSqYa8CIaa4cFczNxmKuGhXMXYrFXHkqmHsfi7nqVDBXignTXAUqmBuCxVwNKphbiMVcRSqY24fFXCUqmLsai7k3mCHlECMTN1iogUJ7BID2uGPJf41UMBdbedFEI3ObqWAutnzdQiNzW6lgLrZ83UYjc9upYC62fN1BI3M7qWAutnzdRSNzAQ+NqM1gCAAA";

    // npc 5259: 5 frames, 79 records, currency 0x03 (Silver)
    private const string AlchemyRecipeVendorCatalogGzip =
        "H4sIAAAAAAACCrXWOywDcRwH8P/d9XFV2tLWZqrEK0h0oJsIo8lA7GaLxSbGGnSxGSUGBgOrpJtnFfWmnvWsZ6m301wqlfw0hv6+/+GS313u8/0t980FjO1lAy4hhOKQUlcxUyeE9seRJKE//++Uy5rWaM7MPy/Ngtw5kDvP4Abd1F1gcOUS6i6C3BCD21RK3SUGt7+KumEGt7qeussgdwXkroLcCMhdA7lOL8ZdZ9jX30zdQKaP9XkDlLPJ4IbbqLsFcrdB7g6D6+ui7i7I3WNwzb3UjXL08SB19xnc6XHqHoDcQ5B7BHKPQe4JyHV5MW6MYd/JUPY+ltL/x6c55lRkyTljcJ1J6p6D3AuQe8ngjngk4l6B3DjIvQa5Nwxuawd1b0HuHYNrH6LuPch9ALkJBjcao+4jyH0Cub/6WJ+ToBy3F+M+g/Z9AbmvDG5Ni0zcN5D7zuD2TFH3A+R+Mrh+n0LcL5CrgVzB8L0FI9SVQK7M4Cb8BuIqINcAchvSfSwrdv2WMcecylTOcJ+R5JhArhnkqiC3GORaQG4eyLWC3HwGt3vURNwCBjeuqsS1MbidExbi2kGuA+QWMrieMStxixjcWouNuN+3HQ1yeBsAAA==";

    // npc 5260: 8 frames, 96 records, currency 0x04 (BindingGold)
    private const string IngredientsVendorCatalogGzip =
        "H4sIAAAAAAACCrXWvU4CQRSG4ZlFE6IWFmhyGjX+xH61FDqBxgRovAE0sRFKoARKCyq0FEqgBK7DO1pjnElMmJVkZ97pCNlTPMWbb7T7eP1WUErtHGr182KlEsfTWv3+v+WdR0ny8ue3/UgHuHuvN+9GAe4+5Dfv7kEO+5DDAeRQgByOIIdjyOEEcjiFHM4gh3/6IFAfBOqDQH3wcviKbI/z23ohUC8E6oVAvRCoFwL1QqBeCNQLL4eR2Rfa7IvYs0sXKS43Ae66XG4D3HW53EEORcihBDmUIYcK5FCFHGqQQx1yaEAOsWeHMvRBoD4I1AcvB7svtNkXRcilBLmUIZcK5FCFHGqQQx1yaEAOdl9EZl80Pbt0meLyFOCuy+U5wF2Xyyvk0IIc2pBDB3LoQg49yKEPOQwghyHk0PTsUIY+CNQHgfrg5WD3RWT2RQtyaUMuHcilCzn0IIc+5DCAHIaQg90XObMvxp5dukpxeQ9w1+XyEeCuy+UTcphADlPIYQY5zCGHBeSwhBxWkMMachh7dihDHwTqg0B98HKw+yJn9sUEcplCLjPIZQ45LCCHJeSwghzWkMM35geo4IAhAAA=";

    // npc 5273: 5 frames, 76 records, currency 0x03 (Silver)
    private const string ForgingRecipeVendorCatalogGzip =
        "H4sIAAAAAAACCrXWOyxDURgH8HNvW61EkGA0kXiFSAzSTYTRZCB2s4HF5rF1cT2KUu9Sj6JovZXr/ayi3onNZraWXKSSz42h3/8MJ/nOyfn9t3+OYqrKcqQKIQzJ0uculCIhIn8sSRLa/X8rW45ESszR+edRO8jtALmdILeLwW1OpK4d5HYzuGoadXtAbi+DK6dT1wFy+xjc0kzq9oNcJ4PbkkfdAZD7wODmF1N3EOQq0T7W5iFQzjDIHQG5oyB3jMG1lVHXBXLHGdxQJXUnQK6bwbXWUncS5E6B3GkG19xI3RmQ6+Ho4zbqzoLcOQY34NHvY+n7fzwfY06OTo4X5C6A3EeQuwhylxhcX5C6PpDrZ3BT3qm7DHJXGFxXhkTcVZC7BnLXGdyKaupugNxNBjfJTt0tkPurj7U5wJDz8kpznkDuNsjdAbkqyN0FuXsMbkG5TNx9kHvA4Db4qXsIco8YXJvVQNxjkHsCck8ZXDVM3TOQe87gttYbiVto/Opj2ZCgHV3EmJOrkxNkcJ1NJuJegtwQyH0GuVcg9xrk3jC4de444oZB7i2D+2axEPcO5N4zuDXeeOJ+AGpYv7dwGgAA";

    // npc 5274: 5 frames, 80 records, currency 0x03 (Silver)
    private const string MythcraftingRecipeVendorCatalogGzip =
        "H4sIAAAAAAACCrXWt0tDURQG8PteDIkIKqijUwQbOgmSTURHJwdj7CX2nlhiTbHlWRJLLDH2iIMODroK+QfEwdnNzdn1qSGPCEdxyPnOcOFwub9vO/cE9HX5oWwhhC5T+jqFpUwI9ZeSJBG7/68KZFWtMCR67VE9yLWC3AYG151O3UaQ28TgRnOo2wxyWxhcOZe6rQxuZR5120BuO4PrKaZuB8jtBLk2BreknLoRkNsFcgOJeRzru0E5PSC3F+T2MbhKFXX7Qe4AyB0EuUMM7nMtdYdB7gjIHWVwzTbqjoFcO8h1MLgGJ3XHQe4EyNXmsRTfjyeTzCn8/v/8NGcK5DpBro/Bfbyl7jTInQG5syB3DuTOg9wFkOticO+fqOsGuR6Q62Vwsz6ouwhyl0Duj3kc65dBOSsMbsQkEXcV5PpArsLg1liouwZy10HuBoObEaTuJsj1g9wAg/v6Rt0tkLsNcq9A7g6DW1otE3cX5GrzWI7vx8Ekc4r+yNljcB0P1N0HuQcMrmLWEfeQwY2+UDcEco8YXK89hbhhkHvM4IZdeuKegNxTkHsNcs9A7jmD+240EveCwbXepRL3ksE13aQR9xOpCQ0m0BsAAA==";

    // npc 5275: 6 frames, 93 records, currency 0x03 (Silver)
    private const string ScholarshipRecipeVendorCatalogGzip =
        "H4sIAAAAAAACCrXWty/FURjG8d/vKpdBSFgleu/96r33evXeu6szCItYWEyMEovBwCrxD4jBbLOZrZfcuDG8yuD9nuEkb07O59mec068uqLPggzD8AgwP3YjJNMwnN8s0zRc53+tGIvTWWz9mt2XQiE3DHLDFVxLsHQjIDdSwS2NkG4U5EYruPvx0o2B3FgFNzFbunGQGw+5CZCbCLlJkJus4B6VS/fkq49dcwqUkwq5aQruY5t00yE3A3IzFVzbuHSzIDcbcnMg1wa5uQqudUu6eZCbr/HeHUu3AHILFdy7K+kWQa67j83P/3HxP3Nif8gpgdxSyC2D3HIF9+ZBuhWQWwm5VQpu4Jt0qyG3BnJrFdyLcFO4dZBbD7kNkNsIuU0KboPd/K2PXXMzlNMCua0Krv+pdNsgtx1yOxTc5xfpdkJuF+TaIbcbcnsgt1fBTaqwCLcPcvsV3NVb6Q5A7iDkuvvY8vk/HvpnTtwPOcMK7pHNQ7gjkDsKuWOQOw65E5A7CblTCu79k3SnIXdGwT1weAp3FnLnIHcechcU3PM9L+EuQu6hp7uP/VzzEpSzDLkrkOuA3FXIXYPcdQV38dJbuBsK7quPj3A3Fdyea1/hbkHuNuTuQO4u5L4Dw1+f8lggAAA=";

    private static readonly Lazy<byte[]> WarriorEquipmentVendorCatalog = new(
        () => InflateCatalog(
            WarriorEquipmentVendorCatalogGzip,
            expectedLength: 5624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5166));

    private static readonly Lazy<byte[]> ScholarEquipmentVendorCatalog = new(
        () => InflateCatalog(
            ScholarEquipmentVendorCatalogGzip,
            expectedLength: 5624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5167));

    private static readonly Lazy<byte[]> JewelryEquipmentVendorCatalog = new(
        () => InflateCatalog(
            JewelryEquipmentVendorCatalogGzip,
            expectedLength: 6784,
            expectedPacketCount: 6,
            expectedCapturedNpcId: 5168));

    private static readonly Lazy<byte[]> ArmorEquipmentVendorCatalog = new(
        () => InflateCatalog(
            ArmorEquipmentVendorCatalogGzip,
            expectedLength: 8120,
            expectedPacketCount: 7,
            expectedCapturedNpcId: 5169));

    private static readonly Lazy<byte[]> WarriorSupplierCatalog = new(
        () => InflateCatalog(
            WarriorSupplierCatalogGzip,
            expectedLength: 5624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5234));

    private static readonly Lazy<byte[]> SkillMerchantCatalog = new(
        () => InflateCatalog(
            SkillMerchantCatalogGzip,
            expectedLength: 7696,
            expectedPacketCount: 8,
            expectedCapturedNpcId: 5237));

    private static readonly Lazy<byte[]> ArmorMerchantCatalog = new(
        () => InflateCatalog(
            ArmorMerchantCatalogGzip,
            expectedLength: 6960,
            expectedPacketCount: 6,
            expectedCapturedNpcId: 5240));

    private static readonly Lazy<byte[]> ScholarSupplierCatalog = new(
        () => InflateCatalog(
            ScholarSupplierCatalogGzip,
            expectedLength: 5624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5245));

    private static readonly Lazy<byte[]> JewelryMerchantCatalog = new(
        () => InflateCatalog(
            JewelryMerchantCatalogGzip,
            expectedLength: 2144,
            expectedPacketCount: 2,
            expectedCapturedNpcId: 5246));

    private static readonly Lazy<byte[]> AlchemyRecipeVendorCatalog = new(
        () => InflateCatalog(
            AlchemyRecipeVendorCatalogGzip,
            expectedLength: 7032,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5259));

    private static readonly Lazy<byte[]> IngredientsVendorCatalog = new(
        () => InflateCatalog(
            IngredientsVendorCatalogGzip,
            expectedLength: 8576,
            expectedPacketCount: 8,
            expectedCapturedNpcId: 5260));

    private static readonly Lazy<byte[]> ForgingRecipeVendorCatalog = new(
        () => InflateCatalog(
            ForgingRecipeVendorCatalogGzip,
            expectedLength: 6768,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5273));

    private static readonly Lazy<byte[]> MythcraftingRecipeVendorCatalog = new(
        () => InflateCatalog(
            MythcraftingRecipeVendorCatalogGzip,
            expectedLength: 7120,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5274));

    private static readonly Lazy<byte[]> ScholarshipRecipeVendorCatalog = new(
        () => InflateCatalog(
            ScholarshipRecipeVendorCatalogGzip,
            expectedLength: 8280,
            expectedPacketCount: 6,
            expectedCapturedNpcId: 5275));

    private static byte[] GetCapturedAthensMerchantCatalogSource(
        CapitalNpcServiceKind service) =>
        service switch
        {
            CapitalNpcServiceKind.AthensWarriorEquipmentVendor =>
                WarriorEquipmentVendorCatalog.Value,
            CapitalNpcServiceKind.AthensScholarEquipmentVendor =>
                ScholarEquipmentVendorCatalog.Value,
            CapitalNpcServiceKind.AthensJewelryEquipmentVendor =>
                JewelryEquipmentVendorCatalog.Value,
            CapitalNpcServiceKind.AthensArmorEquipmentVendor =>
                ArmorEquipmentVendorCatalog.Value,
            CapitalNpcServiceKind.AthensWarriorSupplier =>
                WarriorSupplierCatalog.Value,
            CapitalNpcServiceKind.AthensSkillMerchant =>
                SkillMerchantCatalog.Value,
            CapitalNpcServiceKind.AthensArmorMerchant =>
                ArmorMerchantCatalog.Value,
            CapitalNpcServiceKind.AthensScholarSupplier =>
                ScholarSupplierCatalog.Value,
            CapitalNpcServiceKind.AthensJewelryMerchant =>
                JewelryMerchantCatalog.Value,
            CapitalNpcServiceKind.AthensAlchemyRecipeVendor =>
                AlchemyRecipeVendorCatalog.Value,
            CapitalNpcServiceKind.AthensIngredientsVendor =>
                IngredientsVendorCatalog.Value,
            CapitalNpcServiceKind.AthensForgingRecipeVendor =>
                ForgingRecipeVendorCatalog.Value,
            CapitalNpcServiceKind.AthensMythcraftingRecipeVendor =>
                MythcraftingRecipeVendorCatalog.Value,
            CapitalNpcServiceKind.AthensScholarshipRecipeVendor =>
                ScholarshipRecipeVendorShelf(), // PROF skillbook source: + 4268 while profession.skillbook_shelf is 1
            _ => throw new ArgumentOutOfRangeException(
                nameof(service),
                service,
                "The selected capital NPC is not an Athens merchant.")
        };
}
