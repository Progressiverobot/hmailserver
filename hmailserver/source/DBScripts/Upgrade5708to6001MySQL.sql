insert into hm_settings (settingname, settingstring, settinginteger) values ('ASDMARCEnabled', '', 1);

insert into hm_settings (settingname, settingstring, settinginteger) values ('ASDMARCFailureScore', '', 5);

update hm_dbversion set value = 6001;
