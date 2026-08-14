insert into hm_settings (settingname, settingstring, settinginteger) select 'ASArcFilteringEnabled', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'ASArcFilteringEnabled')

insert into hm_settings (settingname, settingstring, settinginteger) select 'ASArcTrustedSealers', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'ASArcTrustedSealers')

update hm_dbversion set value = 6010
