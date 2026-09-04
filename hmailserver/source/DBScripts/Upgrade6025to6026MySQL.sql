insert into hm_settings (settingname, settingstring, settinginteger) select 'CreateDefaultSpecialUseFolders', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'CreateDefaultSpecialUseFolders');

update hm_dbversion set value = 6026;
