alter table hm_messages add column messageemailid varchar(48) not null default '';

update hm_messages set messageemailid = concat('M0', messageid);

update hm_dbversion set value = 6015;
