insert into hm_settings (settingname, settingstring, settinginteger) select 'ASDMARCEnabled', '', 1 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'ASDMARCEnabled');

insert into hm_settings (settingname, settingstring, settinginteger) select 'ASDMARCFailureScore', '', 5 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'ASDMARCFailureScore');

update hm_dbversion set value = 6001;
